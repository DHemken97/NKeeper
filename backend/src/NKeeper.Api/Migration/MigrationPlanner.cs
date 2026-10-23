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

    /// <summary>
    /// Copy disks on the old host's own storage to the new one. Null: copy for libvirt hosts (a VM
    /// on local storage can't move otherwise), don't for Proxmox (which copies them itself when asked).
    /// </summary>
    public bool? WithLocalDisks { get; init; }

    /// <summary>libvirt: after a successful move, delete the copied disks' old copies on the old host.</summary>
    public bool DeleteOldLocalDisks { get; init; }
}

/// <summary>An image file on the old host's own storage, and whether the new host can take a copy.</summary>
/// <param name="Format">qemu-img's format, e.g. <c>qcow2</c> or <c>raw</c>.</param>
/// <param name="VirtualSize">The disk's size as the VM sees it.</param>
/// <param name="AllocatedSize">The space its data takes on disk (what a copy needs).</param>
/// <param name="FileSize">The file's length (what a copy writes).</param>
/// <param name="BackingFile">The image it's an overlay of, if any (NKeeper doesn't copy backing chains).</param>
/// <param name="TargetFreeBytes">Free space where it would go on the new host.</param>
public sealed record LocalImage(
    string Format, long VirtualSize, long AllocatedSize, long FileSize, string? BackingFile, bool ExistsOnTarget, long? TargetFreeBytes, bool TargetHasQemuImg);

/// <summary>One step of a migration: what it does, on which host ("source", "target", "both" or "hypervisor").</summary>
/// <param name="Kind">One of <see cref="StepKinds"/>.</param>
/// <param name="Critical">A critical step that fails rolls the migration back; others (tidying up the old host) only warn.</param>
public sealed record PlannedStep(string Kind, string Side, string Title, IReadOnlyDictionary<string, string> Args, bool Critical = true);

/// <summary>What the user can choose, and why some choices aren't open.</summary>
/// <param name="LogoutOldIscsi">The choice in effect (the default, when the user didn't choose).</param>
/// <param name="IscsiToLogOut">Targets (and portals) the old host would log out of.</param>
/// <param name="IscsiKept">Targets the old host stays logged in to although asked to log out, with what else uses them.</param>
/// <param name="StorageChecked">Whether NKeeper could see the disks (Proxmox needs SSH logins to the nodes for that).</param>
/// <param name="HasLocalDisks">Whether it has disks on the old host's own storage that can be copied.</param>
/// <param name="CopyLocalDisks">The choice in effect for copying them (the default, when the user didn't choose).</param>
public sealed record PlanChoices(
    bool LiveAvailable,
    string? LiveUnavailableReason,
    bool LogoutOldIscsi,
    IReadOnlyList<string> IscsiToLogOut,
    IReadOnlyList<string> IscsiKept,
    bool HasIscsi,
    bool HasDrbd,
    bool HasLocalDisks,
    bool StorageChecked,
    bool CopyLocalDisks = false);

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
    public const string DiskCreate = "disk-create";
    public const string DiskCopy = "disk-copy";
    public const string DiskDelete = "disk-delete";
}

/// <summary>What the planner knows: the VM, both hosts' storage, and whether the hosts reach each other over SSH.</summary>
/// <param name="Via">"proxmox" or "kvm".</param>
/// <param name="Disks">The VM's disks, traced on the old host; null when there was no login to trace them with (Proxmox only).</param>
/// <param name="PeerSsh">Whether the old host reaches the new one over SSH as root without a prompt (null: not tested), and why not.</param>
/// <param name="ReversePeerSsh">The same, from the new host back to the old one (for rolling back a live move).</param>
/// <param name="Blockers">Problems the hypervisor found (e.g. the VM is already defined on the new host).</param>
/// <param name="LocalImages">libvirt: the image files on the old host's own storage, by path.</param>
internal sealed record PlanInput(
    string Via,
    string GuestName,
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
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, LocalImage>? LocalImages = null);

/// <summary>
/// Plans how a VM's storage moves with it. Offline: make the storage reachable on the new host,
/// shut the VM down, release its storage on the old host (unmount, deactivate LVM, DRBD secondary),
/// take it on the new one (DRBD primary, activate LVM, mount), move the VM and start it. Live: the
/// VM keeps its block devices open on both hosts during the move (DRBD dual-primary for the move),
/// which a file system can't do. Then, as chosen, the old host logs out of the iSCSI targets and
/// takes DRBD down. Every step that changes something has an undo, for rolling back.
/// </summary>
internal static partial class MigrationPlanner
{
    public static MigrationPlan Plan(PlanInput input, MigrationOptions options)
    {
        var blockers = new List<string>(input.Blockers);
        var notes = new List<string>(input.Notes);
        var steps = new List<PlannedStep>();
        var (source, target, vm) = (input.SourceName, input.TargetName, input.GuestName);
        var running = input.State == "running";
        var disks = input.Disks ?? [];
        var logout = options.LogoutOldIscsi ?? input.Via == "kvm";

        // Peers that must reach each other: Proxmox's own migration always; libvirt only to move live.
        if (input.Via == "proxmox" && input.PeerSsh is { Ok: false } proxmoxSsh)
        {
            blockers.Add($"Proxmox moves VMs over SSH between its nodes, and {source} can't reach {target}: {proxmoxSsh.Error}");
        }

        var local = disks.Where(d => d.Layout == DiskLayouts.Local).ToList();
        var copyLocal = options.WithLocalDisks ?? input.Via == "kvm";

        // libvirt: image files on the old host's own storage are copied, if the new host can take them.
        var localFiles = new List<(GuestDisk Disk, LocalImage Image)>();
        foreach (var disk in local)
        {
            var where = disk.Storage is null ? disk.Path : $"{disk.Storage} ({disk.Path})";
            if (input.Via == "kvm")
            {
                // Only a file traced down to the old host's own disks is copied; one that might be on DRBD or iSCSI never is.
                if (!disk.Layers.Any(l => l.Kind == LayerKinds.Local))
                {
                    var why = disk.Note?.IndexOf(": ", StringComparison.Ordinal) is > 0 and var at ? $" ({disk.Note![(at + 2)..].TrimEnd('.')})" : "";
                    blockers.Add($"NKeeper couldn't tell what disk {disk.Name} ({disk.Path}) is stored on{why}, so it won't copy it: it may be on shared storage (DRBD, iSCSI) that moves instead.");
                }
                else if (!StorageTrace.IsLocalFile(disk))
                {
                    blockers.Add($"Disk {disk.Name} ({disk.Path}) is a device on {source}'s own disks; NKeeper can only copy image files.");
                }
                else if (input.LocalImages?.TryGetValue(disk.Path, out var image) == true)
                {
                    localFiles.Add((disk, image));
                }
                else
                {
                    blockers.Add($"NKeeper couldn't read the image file {disk.Path} on {source}.");
                }
            }
            else if (!copyLocal)
            {
                blockers.Add($"Disk {disk.Name} is on {source}'s local storage ({where}). Choose to copy local disks, or move it to shared storage first.");
            }
        }

        if (localFiles.Count > 0 && !copyLocal)
        {
            blockers.Add($"{string.Join(", ", localFiles.Select(f => f.Disk.Path))} {(localFiles.Count == 1 ? "is" : "are")} on {source}'s own storage. Choose to copy {(localFiles.Count == 1 ? "it" : "them")} to {target}, or move {(localFiles.Count == 1 ? "it" : "them")} to shared storage first.");
        }

        foreach (var (disk, image) in localFiles.Where(_ => copyLocal))
        {
            if (image.BackingFile is { } backing)
            {
                blockers.Add($"{disk.Path} is an overlay of {backing}; NKeeper doesn't copy backing chains. Merge it first (qemu-img convert), or copy the chain by hand.");
            }

            if (image.ExistsOnTarget)
            {
                blockers.Add($"{disk.Path} already exists on {target}; NKeeper won't overwrite it.");
            }
        }

        var copyBytes = localFiles.Sum(f => f.Image.AllocatedSize);
        if (copyLocal && localFiles.Count > 0 && localFiles.Min(f => f.Image.TargetFreeBytes) is { } free && free < copyBytes)
        {
            blockers.Add($"{target} has {Size(free)} free where the disks go, but they need {Size(copyBytes)}.");
        }

        if (local.Count > 0 && input.Via == "proxmox" && copyLocal)
        {
            notes.Add("Proxmox copies the disks on local storage to the new node, which takes as long as copying their data.");
        }

        disks = [.. disks.Select(d => localFiles.FirstOrDefault(f => f.Disk == d) is { Image: { } i }
            ? d with { Note = $"{i.Format} image, {Size(i.VirtualSize)} ({Size(i.AllocatedSize)} used)" }
            : d)];

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

        // Proxmox's shared LVM on DRBD: Proxmox activates the volume on the new node itself, while the
        // old one keeps using the volume group for its other VMs, so the resource stays Primary on both.
        foreach (var (resource, storage) in disks.Where(d => d.Layout == DiskLayouts.Managed)
            .SelectMany(d => d.Layers.Where(l => l.Kind == LayerKinds.Drbd).Select(l => (l.Name, d.Storage))).DistinctBy(r => r.Name))
        {
            var there = input.Target?.Drbd?.FirstOrDefault(r => r.Name == resource);
            if (input.Target is { Drbd: null })
            {
                blockers.Add($"Storage {storage} is on DRBD resource {resource}, but {target} has no DRBD tools installed.");
            }
            else if (input.Target is not null && there is null)
            {
                blockers.Add($"Storage {storage} is on DRBD resource {resource}, which isn't configured on {target}.");
            }
            else if (there is not null && (!there.Up || there.Role?.StartsWith("Primary", StringComparison.Ordinal) != true))
            {
                blockers.Add($"Storage {storage} is shared LVM on DRBD resource {resource}, which is {(there.Up ? there.Role ?? "not Primary" : "down")} on {target}. Proxmox activates the disk there itself, so {resource} must be Primary on both nodes (allow-two-primaries) while {source} goes on using it.");
            }
            else if (there?.Volumes.FirstOrDefault(v => v.DiskState != "UpToDate") is { } stale)
            {
                blockers.Add($"DRBD resource {resource} on {target} is {stale.DiskState ?? "unknown"}, not UpToDate. Let it finish syncing first.");
            }
        }

        // A shared disk used by its kernel name (sdb, dm-3, mpatha) may be a different disk on the new host.
        foreach (var disk in disks.Where(d => d.Layout == DiskLayouts.Block && d.Layers.Any(l => l.Kind is LayerKinds.Iscsi or LayerKinds.Multipath) && UnstableName().IsMatch(d.Path)))
        {
            blockers.Add($"Disk {disk.Name} is {disk.Path}, a name the kernel gives disks in the order they appear, so on {target} it may be a different disk. Point the VM at a stable name first (/dev/disk/by-id/…, or the multipath WWID).");
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
            : localFiles.Count > 0 && localFiles.Any(f => !f.Image.TargetHasQemuImg) ? $"Copying its disks while it runs needs qemu-img on {target}, to create them there first."
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
                steps.Add(new(StepKinds.IscsiDiscover, "target", $"Discover the iSCSI targets at portal {portal} on {target} (iscsiadm -m discovery)", args));
            }

            steps.Add(new(StepKinds.IscsiLogin, "target", $"Log in to iSCSI target {layer.Name} at {portal} on {target}", args));
        }

        foreach (var map in wwids.Where(m => input.Target?.Multipath?.Any(t => t.Wwid == m.Name) != true))
        {
            steps.Add(new(StepKinds.MultipathWait, "target", $"Wait for multipath device {map.Detail} (WWID {map.Name}) to appear on {target}", Args(("wwid", map.Name))));
        }

        foreach (var resource in drbd)
        {
            if (input.Target?.Drbd?.FirstOrDefault(r => r.Name == resource.Name) is { Up: false })
            {
                steps.Add(new(StepKinds.DrbdUp, "target", $"Bring DRBD resource {resource.Name} up on {target} (drbdadm up)", Args(("resource", resource.Name))));
            }

            steps.Add(new(StepKinds.DrbdReady, "target", $"Check DRBD resource {resource.Name} is connected and UpToDate on {target}", Args(("resource", resource.Name))));
        }

        foreach (var mount in sharedMounts)
        {
            steps.Add(new(StepKinds.EnsureMounted, "target", $"Mount {mount.MountPoint} ({mount.FsType}) on {target} from its /etc/fstab, if it isn't mounted already", Args(("mountpoint", mount.MountPoint))));
        }

        var diskPaths = input.Via == "kvm" ? disks.Where(d => d.Path.Length > 0).Select(d => d.Path).ToList() : [];
        var paths = string.Join('\n', diskPaths);
        var checkPaths = $"Check {target} sees the VM's {(diskPaths.Count == 1 ? "disk" : "disks")}: {string.Join(", ", diskPaths)}";
        var copying = copyLocal && localFiles.Count > 0;
        var move = Args(("live", live ? "1" : "0"), ("localDisks", copyLocal && local.Count > 0 ? "1" : "0"), ("copyStorage", live && copying ? "1" : "0"));
        if (live)
        {
            foreach (var (disk, image) in copying ? localFiles : [])
            {
                steps.Add(new(StepKinds.DiskCreate, "target", $"Create an empty {image.Format} image of {Size(image.VirtualSize)} at {disk.Path} on {target}, for the live copy (qemu-img create)",
                    Args(("path", disk.Path), ("format", image.Format), ("size", image.VirtualSize.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            }

            foreach (var resource in blockDrbd)
            {
                steps.Add(new(StepKinds.DrbdAllowTwoPrimaries, "both", $"Allow DRBD resource {resource.Name} to be Primary on both {source} and {target} for the move (allow-two-primaries)", Args(("resource", resource.Name))));
                steps.Add(new(StepKinds.DrbdPrimary, "target", $"Make DRBD resource {resource.Name} Primary on {target}", Args(("resource", resource.Name))));
            }

            foreach (var volume in blockLvm)
            {
                steps.Add(new(StepKinds.LvmActivate, "target", $"Activate LVM volume {volume.Name} on {target}", Args(("lv", volume.Name))));
            }

            if (paths.Length > 0)
            {
                steps.Add(new(StepKinds.CheckPaths, "target", checkPaths, Args(("paths", paths))));
            }

            steps.Add(new(StepKinds.GuestMove, "hypervisor", input.Via == "kvm"
                ? copying
                    ? $"Live-migrate {vm} from {source} to {target} while it keeps running, copying its local {(localFiles.Count == 1 ? "disk" : "disks")} ({Size(copyBytes)}) as it goes (virsh migrate --live --copy-storage-all over SSH); its definition moves with it"
                    : $"Live-migrate {vm} from {source} to {target} while it keeps running (virsh migrate --live over SSH); its definition moves with it"
                : $"Ask Proxmox to migrate {vm} from {source} to {target} while it keeps running{(copyLocal && local.Count > 0 ? ", copying its disks on local storage" : "")}", move));
            foreach (var resource in blockDrbd)
            {
                steps.Add(new(StepKinds.DrbdSecondary, "source", $"Make DRBD resource {resource.Name} Secondary on {source}", Args(("resource", resource.Name))));
                steps.Add(new(StepKinds.DrbdDisallowTwoPrimaries, "both", $"Allow DRBD resource {resource.Name} to be Primary on one host only again (on {source} and {target})", Args(("resource", resource.Name))));
            }

            foreach (var volume in blockLvm)
            {
                steps.Add(new(StepKinds.LvmDeactivate, "source", $"Deactivate LVM volume {volume.Name} on {source}", Args(("lv", volume.Name))));
            }
        }
        else
        {
            if (running)
            {
                steps.Add(new(StepKinds.GuestShutdown, "source", $"Shut down {vm} on {source} and wait until it's off", Args()));
            }

            foreach (var (disk, mount) in mounts)
            {
                steps.Add(new(StepKinds.Unmount, "source", $"Unmount {mount.MountPoint} ({mount.FsType} on {mount.Device}) on {source}", MountArgs(disk, mount)));
            }

            foreach (var volume in lvm)
            {
                steps.Add(new(StepKinds.LvmDeactivate, "source", $"Deactivate LVM volume {volume.Name} on {source}", Args(("lv", volume.Name))));
            }

            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdSecondary, "source", $"Make DRBD resource {resource.Name} Secondary on {source}", Args(("resource", resource.Name))));
            }

            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdPrimary, "target", $"Make DRBD resource {resource.Name} Primary on {target}", Args(("resource", resource.Name))));
            }

            foreach (var volume in lvm)
            {
                steps.Add(new(StepKinds.LvmActivate, "target", $"Activate LVM volume {volume.Name} on {target}", Args(("lv", volume.Name))));
            }

            foreach (var (disk, mount) in mounts)
            {
                steps.Add(new(StepKinds.Mount, "target", $"Mount {mount.MountPoint} ({mount.FsType}) on {target}", MountArgs(disk, mount)));
            }

            var direct = input.PeerSsh is { Ok: true };
            foreach (var (disk, image) in copying ? localFiles : [])
            {
                steps.Add(new(StepKinds.DiskCopy, "target",
                    $"Copy {disk.Path} ({Size(image.FileSize)}) from {source} to {target}, "
                    + (direct ? $"directly from {source} over SSH (rsync, or dd over ssh)" : $"through NKeeper, since {source} can't reach {target} over SSH (slower)")
                    + ", then check its size there",
                    Args(("path", disk.Path), ("size", image.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("direct", direct ? "1" : "0"))));
            }

            if (paths.Length > 0)
            {
                steps.Add(new(StepKinds.CheckPaths, "target", checkPaths, Args(("paths", paths))));
            }

            steps.Add(new(StepKinds.GuestMove, "hypervisor", input.Via == "kvm"
                ? $"Copy {vm}'s libvirt definition from {source} to {target} (virsh dumpxml, then virsh define), then remove it from {source} (virsh undefine)"
                : $"Ask Proxmox to move {vm} from {source} to {target} while it's off{(copyLocal && local.Count > 0 ? ", copying its disks on local storage" : "")}", move));
            if (running && options.StartAfter)
            {
                steps.Add(new(StepKinds.GuestStart, "target", $"Start {vm} on {target}", Args()));
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
                    steps.Add(new(StepKinds.MultipathFlush, "source", $"Remove multipath device {map.Detail} (WWID {map.Name}) on {source}, unless it's still in use", Args(("wwid", map.Name), ("map", map.Detail ?? map.Name)), Critical: false));
                }
            }

            steps.Add(new(StepKinds.IscsiLogout, "source", $"Log out of iSCSI target {layer.Name} at {layer.Detail} on {source}, unless its disks are still in use", Args(("target", layer.Name), ("portal", layer.Detail!)), Critical: false));
        }

        if (logout && disks.Any(d => d.Layout == DiskLayouts.Managed && d.Layers.Any(l => l.Kind == LayerKinds.Iscsi)))
        {
            notes.Add("Proxmox logs its nodes in to the iSCSI storage it manages again by itself.");
        }

        if (options.DrbdDownOnOld)
        {
            foreach (var resource in drbd)
            {
                steps.Add(new(StepKinds.DrbdDown, "source", $"Take DRBD resource {resource.Name} down on {source} (drbdadm down)", Args(("resource", resource.Name)), Critical: false));
            }
        }

        if (copying && options.DeleteOldLocalDisks && input.Via == "kvm")
        {
            foreach (var (disk, _) in localFiles)
            {
                steps.Add(new(StepKinds.DiskDelete, "source", $"Delete the old copy of {disk.Path} on {source}", Args(("path", disk.Path)), Critical: false));
            }
        }

        var hasLocal = input.Via == "kvm" ? localFiles.Count > 0 : local.Count > 0;
        return new MigrationPlan(
            live ? "live" : "offline",
            input.State,
            source,
            target,
            disks,
            steps,
            blockers,
            notes,
            new PlanChoices(noLive is null, noLive, logout, toLogOut, kept, iscsi.Count > 0, drbd.Count > 0, hasLocal, input.Disks is not null, copyLocal && hasLocal));
    }

    /// <summary>A size for people, e.g. "12.3 GiB".</summary>
    internal static string Size(long bytes)
    {
        string[] units = ["bytes", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} bytes" : $"{value.ToString(value >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
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

    /// <summary>A disk's kernel name (or a multipath alias), which can differ between hosts.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^/dev/(?:sd[a-z]+\d*|dm-\d+|mapper/mpath[a-z]+(?:-?part\d+)?)$")]
    private static partial System.Text.RegularExpressions.Regex UnstableName();
}
