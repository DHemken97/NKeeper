using NKeeper.Api.Storage;

namespace NKeeper.Api.Migration;

/// <summary>What the user chose for a migration.</summary>
public sealed record MigrationOptions
{
    /// <summary>Move a running VM while it runs, when its storage allows; otherwise it is shut down, moved and started again.</summary>
    public bool Live { get; init; } = true;

    /// <summary>After an offline move, start the VM on the new host if it was running.</summary>
    public bool StartAfter { get; init; } = true;

    /// <summary>
    /// After the move, log the old host out of the iSCSI targets the VM used (those nothing else
    /// there uses). Null: log out for libvirt hosts (often one host per volume), stay logged in for
    /// Proxmox nodes (usually all logged in to all).
    /// </summary>
    public bool? LogoutOldIscsi { get; init; }

    /// <summary>After the move, take the VM's DRBD resources down on the old host instead of leaving it Secondary.</summary>
    public bool DrbdDownOnOld { get; init; }

    /// <summary>Proxmox: copy disks on the old node's local storage.</summary>
    public bool WithLocalDisks { get; init; }
}

/// <summary>One step of a migration: what it does, on which host ("source", "target", "both" or "hypervisor").</summary>
/// <param name="Kind">One of <see cref="StepKinds"/>.</param>
/// <param name="Critical">A critical step that fails rolls the migration back; others (tidying up the old host) only warn.</param>
public sealed record PlannedStep(string Kind, string Side, string Title, IReadOnlyDictionary<string, string> Args, bool Critical = true);

/// <summary>What the user can choose, and why some choices aren't open.</summary>
/// <param name="LogoutOldIscsi">The choice in effect (the default, when the user didn't choose).</param>
/// <param name="IscsiToLogOut">Targets (and portals) the old host would log out of.</param>
/// <param name="IscsiKept">Targets the old host stays logged in to although asked to log out, with what else uses them.</param>
public sealed record PlanChoices(
    bool LiveAvailable,
    string? LiveUnavailableReason,
    bool LogoutOldIscsi,
    IReadOnlyList<string> IscsiToLogOut,
    IReadOnlyList<string> IscsiKept,
    bool HasIscsi,
    bool HasDrbd,
    bool HasLocalDisks);

/// <summary>A migration as it would run: "live" or "offline", the disks, and the steps; it can't start while there are blockers.</summary>
public sealed record MigrationPlan(
    string Mode,
    string GuestState,
    string SourceName,
    string TargetName,
    IReadOnlyList<GuestDisk> Disks,
    IReadOnlyList<PlannedStep> Steps,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes,
    PlanChoices Choices);

public static class StepKinds
{
    public const string IscsiDiscover = "iscsi-discover";
    public const string IscsiLogin = "iscsi-login";
    public const string IscsiLogout = "iscsi-logout";
    public const string MultipathWait = "multipath-wait";
    public const string MultipathFlush = "multipath-flush";
    public const string DrbdUp = "drbd-up";
    public const string DrbdReady = "drbd-ready";
    public const string DrbdPrimary = "drbd-primary";
    public const string DrbdSecondary = "drbd-secondary";
    public const string DrbdDown = "drbd-down";
    public const string DrbdAllowTwoPrimaries = "drbd-allow-two";
    public const string DrbdDisallowTwoPrimaries = "drbd-disallow-two";
    public const string LvmActivate = "lvm-activate";
    public const string LvmDeactivate = "lvm-deactivate";
    public const string Mount = "mount";
    public const string Unmount = "umount";
    public const string EnsureMounted = "ensure-mounted";
    public const string CheckPaths = "check-paths";
    public const string GuestShutdown = "guest-shutdown";
    public const string GuestMove = "guest-move";
    public const string GuestStart = "guest-start";
}

/// <summary>What the planner knows: the VM, both hosts' storage, and whether the hosts reach each other over SSH.</summary>
/// <param name="Via">"proxmox" or "kvm".</param>
/// <param name="Disks">The VM's disks, traced on the old host; null when there was no login to trace them with (Proxmox only).</param>
/// <param name="PeerSsh">Whether the old host reaches the new one over SSH as root without a prompt (null: not tested), and why not.</param>
/// <param name="ReversePeerSsh">The same, from the new host back to the old one (for rolling back a live move).</param>
/// <param name="Blockers">Problems the hypervisor found (e.g. the VM is already defined on the new host).</param>
internal sealed record PlanInput(
    string Via,
    bool IsContainer,
    string State,
    string SourceName,
    string TargetName,
    IReadOnlyList<GuestDisk>? Disks,
    IReadOnlyDictionary<(string Target, string Portal), string> IscsiInUse,
    StorageStatus? Source,
    StorageStatus? Target,
    (bool Ok, string? Error)? PeerSsh,
    (bool Ok, string? Error)? ReversePeerSsh,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes);

/// <summary>
/// Plans how a VM's storage moves with it. Offline: make the storage reachable on the new host,
/// shut the VM down, release its storage on the old host (unmount, deactivate LVM, DRBD secondary),
/// take it on the new one (DRBD primary, activate LVM, mount), move the VM and start it. Live: the
/// VM keeps its block devices open on both hosts during the move (DRBD dual-primary for the move),
/// which a file system can't do. Then, as chosen, the old host logs out of the iSCSI targets and
/// takes DRBD down. Every step that changes something has an undo, for rolling back.
/// </summary>
internal static class MigrationPlanner
{
    public static MigrationPlan Plan(PlanInput input, MigrationOptions options)
    {
        var blockers = new List<string>(input.Blockers);
        var notes = new List<string>(input.Notes);
        var steps = new List<PlannedStep>();
        var (source, target) = (input.SourceName, input.TargetName);
        var running = input.State == "running";
        var disks = input.Disks ?? [];
        var logout = options.LogoutOldIscsi ?? input.Via == "kvm";

        // Peers that must reach each other: Proxmox's own migration always; libvirt only to move live.
        if (input.Via == "proxmox" && input.PeerSsh is { Ok: false } proxmoxSsh)
        {
            blockers.Add($"Proxmox moves VMs over SSH between its nodes, and {source} can't reach {target}: {proxmoxSsh.Error}");
        }

        var local = disks.Where(d => d.Layout == DiskLayouts.Local).ToList();
        foreach (var disk in local)
        {
            var where = disk.Storage is null ? disk.Path : $"{disk.Storage} ({disk.Path})";
            if (input.Via == "kvm")
            {
                blockers.Add($"Disk {disk.Name} is on {source}'s own storage ({where}); NKeeper can only move VMs whose disks are on shared storage.");
            }
            else if (!options.WithLocalDisks)
            {
                blockers.Add($"Disk {disk.Name} is on {source}'s local storage ({where}). Choose to copy local disks, or move it to shared storage first.");
            }
        }

        if (local.Count > 0 && input.Via == "proxmox" && options.WithLocalDisks)
        {
            notes.Add("Proxmox copies the disks on local storage to the new node, which takes as long as copying their data.");
        }

        // What moves, layer by layer.
        var handoff = disks.Where(d => d.Layout is DiskLayouts.Filesystem or DiskLayouts.Block).ToList();
        var reachable = disks.Where(d => d.Layout is DiskLayouts.Filesystem or DiskLayouts.Block or DiskLayouts.Managed or DiskLayouts.Shared).ToList();
        var iscsi = reachable.SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Iscsi).Distinct().ToList();
        var wwids = reachable.SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Multipath).DistinctBy(l => l.Name).ToList();
        var drbd = handoff.SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Drbd).DistinctBy(l => l.Name).ToList();
        var blockDrbd = handoff.Where(d => d.Layout == DiskLayouts.Block).SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Drbd).DistinctBy(l => l.Name).ToList();
        var lvm = handoff.SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Lvm).DistinctBy(l => l.Name).ToList();
        var blockLvm = handoff.Where(d => d.Layout == DiskLayouts.Block).SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Lvm).DistinctBy(l => l.Name).ToList();
        var mounts = handoff.Where(d => d.Layout == DiskLayouts.Filesystem && d.Mount is not null).Select(d => (Disk: d, Mount: d.Mount!)).DistinctBy(m => m.Mount.MountPoint).ToList();
        var sharedMounts = disks.Where(d => d.Layout is DiskLayouts.Shared or DiskLayouts.Network && d.Storage is null && d.Mount is not null).Select(d => d.Mount!).DistinctBy(m => m.MountPoint).ToList();

        foreach (var layer in iscsi.Where(l => l.Detail is null))
        {
            blockers.Add($"Couldn't tell which iSCSI target {source}'s disk {layer.Name} belongs to.");
        }

        if (iscsi.Count > 0 && input.Target is { Iscsi: null })
        {
            blockers.Add($"The VM's disks are on iSCSI, but {target} has no iSCSI initiator (iscsiadm) installed.");
        }

        if (wwids.Count > 0 && input.Target is { Multipath: null })
        {
            blockers.Add($"The VM's disks are on multipath devices, but {target} has no multipath tools installed.");
        }

        foreach (var resource in drbd)
        {
            var there = input.Target?.Drbd?.FirstOrDefault(r => r.Name == resource.Name);
            if (input.Target is { Drbd: null })
            {
                blockers.Add($"The VM's disks are on DRBD resource {resource.Name}, but {target} has no DRBD tools installed.");
            }
            else if (input.Target is not null && there is null)
            {
                blockers.Add($"DRBD resource {resource.Name} isn't configured on {target}.");
            }
            else if (there is { Up: true } && there.Volumes.FirstOrDefault(v => v.DiskState != "UpToDate") is { } stale)
            {
                blockers.Add($"DRBD resource {resource.Name} on {target} is {stale.DiskState ?? "unknown"}, not UpToDate. Let it finish syncing first.");
            }
        }

        foreach (var (disk, mount) in mounts)
        {
            notes.Add($"{mount.MountPoint} ({mount.FsType} on {mount.Device}) can be mounted on one host at a time: anything else on {source} using it must stop, or unmounting it fails and the migration rolls back.");
        }

        // Live, if the storage and the hosts allow it.
        string? noLive = !running ? "It isn't running."
            : input.IsContainer ? "Containers can't move while running; it's restarted on the new node."
            : mounts.Count > 0 ? $"Its disks are image files on {string.Join(", ", mounts.Select(m => m.Mount.MountPoint))}, a file system that can't be mounted on two hosts at once."
            : input.Via == "kvm" && input.PeerSsh is not { Ok: true } ? $"Live moves need {source} to reach {target} over SSH as root without a password{(input.PeerSsh?.Error is { } e ? $" ({e})" : "")}."
            : null;
        var live = options.Live && noLive is null;
        if (options.Live && running && noLive is not null)
        {
            notes.Add($"It will be shut down for the move: {noLive}");
        }

        if (live && input.Via == "kvm" && input.ReversePeerSsh is { Ok: false } back)
        {
            notes.Add($"If this move had to be rolled back, {target} couldn't move it back ({back.Error}); it would stay on {target}.");
        }

        if (input.Disks is null)
        {
            notes.Add("NKeeper didn't check the storage: it needs an SSH login to both nodes for that. Proxmox checks its own storage.");
        }

        // Storage the new host must see before anything stops.
        foreach (var layer in iscsi.Where(l => l.Detail is not null))
        {
            var portal = layer.Detail!;
            if (input.Target?.Iscsi?.Sessions.Any(s => s.Target == layer.Name && s.Portal == portal) == true)
            {
                continue;
            }

            var args = Args(("target", layer.Name), ("portal", portal));
            if (input.Target?.Iscsi?.Nodes.Any(n => n.Target == layer.Name && n.Portal == portal) != true)
            {
                steps.Add(new(StepKinds.IscsiDiscover, "target", $"Discover iSCSI targets at {portal} on {target}", args));
            }

            steps.Add(new(StepKinds.IscsiLogin, "target", $"Log {target} in to {layer.Name} at {portal}", args));
        }

        foreach (var map in wwids.Where(m => input.Target?.Multipath?.Any(t => t.Wwid == m.Name) != true))
        {
            steps.Add(new(StepKinds.MultipathWait, "target", $"Wait for multipath map {map.Detail} ({map.Name}) on {target}", Args(("wwid", map.Name))));
        }

        foreach (var resource in drbd)
        {
            if (input.Target?.Drbd?.FirstOrDefault(r => r.Name == resource.Name) is { Up: false })
            {
                steps.Add(new(StepKinds.DrbdUp, "target", $"Bring DRBD resource {resource.Name} up on {target}", Args(("resource", resource.Name))));
            }

            steps.Add(new(StepKinds.DrbdReady, "target", $"Check {resource.Name} is connected and UpToDate on {target}", Args(("resource", resource.Name))));
        }

        foreach (var mount in sharedMounts)
        {
            steps.Add(new(StepKinds.EnsureMounted, "target", $"Make sure {mount.MountPoint} is mounted on {target}", Args(("mountpoint", mount.MountPoint))));
        }

        var paths = input.Via == "kvm" ? string.Join('\n', disks.Where(d => d.Path.Length > 0).Select(d => d.Path)) : "";
        var move = Args(("live", live ? "1" : "0"), ("localDisks", options.WithLocalDisks ? "1" : "0"));
        if (live)
        {
            foreach (var resource in blockDrbd)
            {
                steps.Add(new(StepKinds.DrbdAllowTwoPrimaries, "both", $"Let {resource.Name} be primary on both hosts during the move", Args(("resource", resource.Name))));
                steps.Add(new(StepKinds.DrbdPrimary, "target", $"Make {resource.Name} primary on {target}", Args(("resource", resource.Name))));
            }

            foreach (var volume in blockLvm)
            {
                steps.Add(new(StepKinds.LvmActivate, "target", $"Activate {volume.Name} on {target}", Args(("lv", volume.Name))));
            }

            if (paths.Length > 0)
            {
                steps.Add(new(StepKinds.CheckPaths, "target", $"Check {target} sees every disk", Args(("paths", paths))));
            }

            steps.Add(new(StepKinds.GuestMove, "hypervisor", $"Move it to {target} while it runs", move));
            foreach (var resource in blockDrbd)
            {
                steps.Add(new(StepKinds.DrbdSecondary, "source", $"Make {resource.Name} secondary on {source}", Args(("resource", resource.Name))));
                steps.Add(new(StepKinds.DrbdDisallowTwoPrimaries, "both", $"Let {resource.Name} be primary on one host again", Args(("resource", resource.Name))));
            }

            foreach (var volume in blockLvm)
            {
                steps.Add(new(StepKinds.LvmDeactivate, "source", $"Deactivate {volume.Name} on {source}", Args(("lv", volume.Name))));
            }
        }
        else
        {
            if (running)
            {
                steps.Add(new(StepKinds.GuestShutdown, "source", $"Shut it down on {source}", Args()));
            }

            foreach (var (disk, mount) in mounts)
            {
                steps.Add(new(StepKinds.Unmount, "source", $"Unmount {mount.MountPoint} on {source}", MountArgs(disk, mount)));
            }

            foreach (var volume in lvm)
            {
                steps.Add(new(StepKinds.LvmDeactivate, "source", $"Deactivate {volume.Name} on {source}", Args(("lv", volume.Name))));
            }

            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdSecondary, "source", $"Make {resource.Name} secondary on {source}", Args(("resource", resource.Name))));
            }

            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdPrimary, "target", $"Make {resource.Name} primary on {target}", Args(("resource", resource.Name))));
            }

            foreach (var volume in lvm)
            {
                steps.Add(new(StepKinds.LvmActivate, "target", $"Activate {volume.Name} on {target}", Args(("lv", volume.Name))));
            }

            foreach (var (disk, mount) in mounts)
            {
                steps.Add(new(StepKinds.Mount, "target", $"Mount {mount.MountPoint} on {target}", MountArgs(disk, mount)));
            }

            if (paths.Length > 0)
            {
                steps.Add(new(StepKinds.CheckPaths, "target", $"Check {target} sees every disk", Args(("paths", paths))));
            }

            steps.Add(new(StepKinds.GuestMove, "hypervisor", $"Move it to {target}", move));
            if (running && options.StartAfter)
            {
                steps.Add(new(StepKinds.GuestStart, "target", $"Start it on {target}", Args()));
            }
        }

        // Tidying up the old host, as chosen; a failure here only warns.
        var toLogOut = new List<string>();
        var kept = new List<string>();
        foreach (var layer in iscsi.Where(l => l.Detail is not null))
        {
            var name = $"{layer.Name} at {layer.Detail}";
            if (!logout)
            {
                continue;
            }

            if (input.IscsiInUse.TryGetValue((layer.Name, layer.Detail!), out var users))
            {
                kept.Add($"{name} (also used by {users})");
                continue;
            }

            toLogOut.Add(name);
            foreach (var map in reachable.Where(d => d.Layers.Contains(layer)).SelectMany(d => d.Layers).Where(l => l.Kind == LayerKinds.Multipath).DistinctBy(l => l.Name))
            {
                if (!steps.Any(s => s.Kind == StepKinds.MultipathFlush && s.Args["wwid"] == map.Name))
                {
                    steps.Add(new(StepKinds.MultipathFlush, "source", $"Remove multipath map {map.Detail} on {source}", Args(("wwid", map.Name), ("map", map.Detail ?? map.Name)), Critical: false));
                }
            }

            steps.Add(new(StepKinds.IscsiLogout, "source", $"Log {source} out of {layer.Name} at {layer.Detail}", Args(("target", layer.Name), ("portal", layer.Detail!)), Critical: false));
        }

        if (logout && disks.Any(d => d.Layout == DiskLayouts.Managed && d.Layers.Any(l => l.Kind == LayerKinds.Iscsi)))
        {
            notes.Add("Proxmox logs its nodes in to the iSCSI storage it manages again by itself.");
        }

        if (options.DrbdDownOnOld)
        {
            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdDown, "source", $"Take {resource.Name} down on {source}", Args(("resource", resource.Name)), Critical: false));
            }
        }

        var hasLocal = local.Count > 0;
        return new MigrationPlan(
            live ? "live" : "offline",
            input.State,
            source,
            target,
            disks,
            steps,
            blockers,
            notes,
            new PlanChoices(noLive is null, noLive, logout, toLogOut, kept, iscsi.Count > 0, drbd.Count > 0, hasLocal));
    }

    /// <summary>How a file system is mounted on either host: its device is found again there by DRBD resource, LVM volume, multipath WWID or file system UUID.</summary>
    private static Dictionary<string, string> MountArgs(GuestDisk disk, StorageMount mount)
    {
        var top = disk.Layers.Count > 0 ? disk.Layers[0] : null;
        var args = Args(("mountpoint", mount.MountPoint), ("fstype", mount.FsType), ("device", mount.Device));
        switch (top?.Kind)
        {
            case LayerKinds.Drbd:
                args["drbd"] = top.Name;
                break;
            case LayerKinds.Lvm:
                args["lv"] = top.Name;
                break;
            case LayerKinds.Multipath:
                args["wwid"] = top.Name;
                break;
            default:
                if (mount.Uuid is { } uuid)
                {
                    args["uuid"] = uuid;
                }

                break;
        }

        return args;
    }

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
