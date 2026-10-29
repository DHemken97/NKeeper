using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Storage;

/// <summary>
/// An iSCSI session to a target through a portal: its state and the disks it attached. Sessions to
/// the same target through the same portal are one (a SAN may open several, one per data address).
/// </summary>
/// <param name="Portal">
/// The portal it was logged in through (iscsiadm's persistent portal, which its node record is
/// under), e.g. <c>10.0.0.5:3260</c>, without the portal group. A SAN that redirects logins (Nimble,
/// EqualLogic…) connects it to another address, in <paramref name="ConnectedTo"/>.
/// </param>
/// <param name="State">The session state, e.g. <c>LOGGED_IN</c>, <c>FAILED</c> or <c>FREE</c>.</param>
/// <param name="Disks">The SCSI disks it attached, e.g. <c>sdb</c>.</param>
/// <param name="ConnectedTo">The addresses the session actually connected to, when the SAN redirected it from <paramref name="Portal"/>.</param>
public sealed record IscsiSession(string Target, string Portal, string State, IReadOnlyList<string> Disks, IReadOnlyList<string>? ConnectedTo = null);

/// <summary>A target the initiator knows (found by discovery, or added by hand), and whether a session is logged in to it.</summary>
public sealed record IscsiNode(string Target, string Portal, bool LoggedIn);

/// <summary>The iSCSI initiator: its name, sessions and known targets.</summary>
public sealed record IscsiStatus(string? InitiatorName, IReadOnlyList<IscsiSession> Sessions, IReadOnlyList<IscsiNode> Nodes);

/// <summary>A path of a multipath map: the SCSI device and its states.</summary>
/// <param name="Address">The SCSI address (host:channel:id:lun), e.g. <c>3:0:0:0</c>.</param>
/// <param name="DmState">device-mapper's view: <c>active</c> or <c>failed</c>.</param>
/// <param name="PathState">multipath's checker: <c>ready</c>, <c>ghost</c>, <c>faulty</c>, <c>shaky</c>…</param>
/// <param name="OnlineState">The kernel's: <c>running</c>, <c>offline</c>…</param>
public sealed record MultipathPath(string Address, string Device, string DmState, string PathState, string OnlineState);

/// <summary>A multipath map (a device-mapper device over several paths to the same LUN).</summary>
/// <param name="Name">Its alias (e.g. <c>mpatha</c>), or its WWID when it has none.</param>
/// <param name="DmDevice">The device-mapper device, e.g. <c>dm-0</c>.</param>
/// <param name="Product">Vendor and product, e.g. <c>LIO-ORG,disk01</c>.</param>
public sealed record MultipathMap(string Name, string Wwid, string DmDevice, string? Product, string? Size, IReadOnlyList<MultipathPath> Paths);

/// <summary>A volume of a DRBD resource on this node.</summary>
/// <param name="DiskState">e.g. <c>UpToDate</c>, <c>Inconsistent</c>, <c>Diskless</c>.</param>
public sealed record DrbdVolume(int Volume, int? Minor, string? DiskState);

/// <summary>A peer's copy of a volume.</summary>
/// <param name="ReplicationState">e.g. <c>Established</c>, <c>SyncSource</c>, <c>SyncTarget</c>.</param>
/// <param name="PercentInSync">How much of the peer's copy is in sync, when DRBD says.</param>
public sealed record DrbdPeerVolume(int Volume, string? ReplicationState, string? DiskState, double? PercentInSync);

/// <summary>A connection of a resource to a peer node.</summary>
/// <param name="ConnectionState">e.g. <c>Connected</c>, <c>Connecting</c>, <c>StandAlone</c>.</param>
public sealed record DrbdPeer(string Name, string ConnectionState, string? Role, IReadOnlyList<DrbdPeerVolume> Volumes);

/// <summary>A DRBD resource configured on this node; one that isn't up has no role, volumes or peers.</summary>
/// <param name="Role">This node's role: <c>Primary</c>, <c>Secondary</c> or <c>Unknown</c>; null when the resource is down.</param>
public sealed record DrbdResource(string Name, bool Up, string? Role, IReadOnlyList<DrbdVolume> Volumes, IReadOnlyList<DrbdPeer> Peers);

/// <summary>A Proxmox LVM storage (<c>lvm:</c> in <c>/etc/pve/storage.cfg</c>, which the whole cluster shares).</summary>
/// <param name="Nodes">The nodes it's restricted to; empty for every node.</param>
public sealed record ProxmoxLvmStorage(string Id, string VolumeGroup, IReadOnlyList<string> Nodes, bool Disabled);

/// <summary>What's on a multipath map, as Proxmox LVM storage sees it.</summary>
/// <param name="VolumeGroup">The LVM volume group it's a physical volume of.</param>
/// <param name="Signature">What else is on it (a file system or partition table), when it isn't an LVM volume group.</param>
/// <param name="Storage">The Proxmox LVM storage on its volume group, for any node.</param>
/// <param name="Mounted">That storage is enabled on this node: Proxmox uses the map here.</param>
public sealed record LvmMap(string Map, string? VolumeGroup, string? Signature, string? Storage, bool Mounted);

/// <summary>A Proxmox node's LVM storage on its multipath maps.</summary>
/// <param name="Node">This node's name in the cluster.</param>
/// <param name="ClusterNodes">Every node of the cluster (just this one when it isn't clustered).</param>
/// <param name="AllStorage">Every storage ID in the cluster, of any type (they can't repeat).</param>
public sealed record ProxmoxLvm(string Node, IReadOnlyList<string> ClusterNodes, IReadOnlyList<string> AllStorage, IReadOnlyList<ProxmoxLvmStorage> Storages, IReadOnlyList<LvmMap> Maps);

/// <summary>What a host's shared storage looks like; each part is null when its tools aren't installed (<paramref name="Proxmox"/>: on a host that isn't a Proxmox node).</summary>
public sealed record StorageStatus(IscsiStatus? Iscsi, IReadOnlyList<MultipathMap>? Multipath, IReadOnlyList<DrbdResource>? Drbd, ProxmoxLvm? Proxmox = null);

/// <summary>The storage actions NKeeper offers.</summary>
public static class StorageActions
{
    public const string IscsiDiscover = "iscsi.discover";
    public const string IscsiLogin = "iscsi.login";
    public const string IscsiLogout = "iscsi.logout";
    public const string IscsiRescan = "iscsi.rescan";
    public const string MultipathReload = "multipath.reload";
    public const string MultipathFlush = "multipath.flush";
    public const string DrbdUp = "drbd.up";
    public const string DrbdDown = "drbd.down";
    public const string DrbdPrimary = "drbd.primary";
    public const string DrbdSecondary = "drbd.secondary";
    public const string DrbdConnect = "drbd.connect";
    public const string DrbdDisconnect = "drbd.disconnect";
    public const string DrbdAdjust = "drbd.adjust";
    public const string LvmMount = "lvm.mount";
    public const string LvmUnmount = "lvm.unmount";
}

/// <summary>An action's shell command (run as root) and how the audit log describes it.</summary>
internal sealed record StorageCommand(string Script, string Description);

/// <summary>
/// A Linux host's shared storage, over SSH: iSCSI sessions and targets (<c>iscsiadm</c>), multipath
/// maps (<c>multipath</c>) and DRBD resources (<c>drbdsetup</c>, <c>drbdadm</c>). Reading them and
/// changing them needs root, or sudo without a password. Every value put in a command line is
/// checked against what the host itself reported (or, for discovery, a strict address format).
/// </summary>
internal static partial class StorageHost
{
    /// <summary>Sets up the tools' usual folders and <c>$p</c>, the prefix that runs a command as root.</summary>
    internal const string AsRoot = "PATH=$PATH:/usr/sbin:/sbin; p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; ";

    /// <summary>
    /// Prints each installed tool's state after an <c>@@section</c> line, or only <c>@@noroot</c> when
    /// a tool is installed but the user can't become root.
    /// </summary>
    public const string StatusScript =
        AsRoot
        + "have() { command -v \"$1\" >/dev/null 2>&1; }; "
        + "if [ -n \"$p\" ] && { have iscsiadm || have multipath || have drbdadm || have pvesm; } && ! sudo -n true 2>/dev/null; then echo '@@noroot'; exit 0; fi; "
        + "if have iscsiadm; then echo '@@iscsi-initiator'; $p sed -n 's/^InitiatorName=//p' /etc/iscsi/initiatorname.iscsi 2>/dev/null; "
        + "echo '@@iscsi-sessions'; $p iscsiadm -m session -P 3 2>/dev/null; echo '@@iscsi-nodes'; $p iscsiadm -m node 2>/dev/null; fi; "
        + "if have multipath; then echo '@@multipath'; $p multipath -ll 2>/dev/null; fi; "
        + "if have drbdadm; then echo '@@drbd-resources'; $p drbdadm sh-resources 2>/dev/null; "
        + "echo '@@drbd-json'; $p drbdsetup status --json 2>/dev/null || { echo '@@drbd-text'; $p drbdsetup status --verbose 2>/dev/null; }; "
        // Each resource's devices by its configuration, in volume order: DRBD 8.4's status has no minors.
        + "echo '@@drbd-devices'; for r in $($p drbdadm sh-resources 2>/dev/null); do for d in $($p drbdadm sh-dev \"$r\" 2>/dev/null); do echo \"$r $d\"; done; done; fi; "
        // A Proxmox node: its name, the cluster's nodes and storage, LVM's physical volumes (as named and resolved) and what's on each device-mapper device.
        + "if have pvesm && have multipath; then echo '@@pve-node'; uname -n | cut -d. -f1; echo '@@pve-nodes'; $p ls /etc/pve/nodes 2>/dev/null; "
        + "echo '@@pve-storage'; $p cat /etc/pve/storage.cfg 2>/dev/null; "
        + "echo '@@lvm-pvs'; $p pvs --noheadings --separator '|' -o pv_name,vg_name 2>/dev/null | while IFS='|' read -r pv vg; do echo \"$(echo $pv)|$(readlink -f $pv 2>/dev/null)|$vg\"; done; "
        + "echo '@@signatures'; $p lsblk -Pdno KNAME,FSTYPE,PTTYPE 2>/dev/null; fi; "
        + "exit 0";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>iSCSI logins and discovery wait for the target; give them longer than iscsiadm's own timeouts.</summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(150);

    /// <summary>The host's storage now.</summary>
    public static async Task<StorageStatus> GetStatusAsync(SshClient client, CancellationToken cancellationToken) =>
        ParseStatus(await RunAsync(client, StatusScript, StatusTimeout, "Reading the host's storage", cancellationToken));

    /// <summary>Runs an action's command; returns everything it printed (its errors and warnings too).</summary>
    public static Task<string> RunActionAsync(SshClient client, StorageCommand command, CancellationToken cancellationToken) =>
        RunAsync(client, AsRoot + command.Script, ActionTimeout, command.Description, cancellationToken, withErrors: true);

    /// <summary>
    /// Why an action that exited cleanly didn't do what it should have, judged by the host's storage
    /// afterwards (<paramref name="after"/>); null when it did. A tool can exit 0 without having
    /// done it, so success is never taken from the exit code alone.
    /// </summary>
    internal static string? Unachieved(string action, string? target, string? portal, string? name, StorageStatus after)
    {
        var at = NormalizePortal(portal);
        var session = after.Iscsi?.Sessions.FirstOrDefault(s => s.Target == target && s.Portal == at);
        var map = after.Multipath?.FirstOrDefault(m => m.Name == name);
        var resource = after.Drbd?.FirstOrDefault(r => r.Name == name);
        var lvm = after.Proxmox?.Maps.FirstOrDefault(m => m.Map == name);
        return action switch
        {
            StorageActions.IscsiLogin when session is null =>
                after.Iscsi?.Sessions.Any(s => s.Target == target) == true
                    ? $"the host has a session to {target}, but not through {at}"
                    : $"the host has no session to {target} at {at} afterwards",
            StorageActions.IscsiLogin when session.State != "LOGGED_IN" => $"the session to {target} is {session.State}, not logged in",
            StorageActions.IscsiLogout when session is not null => $"the host is still logged in to {target} at {at}",
            StorageActions.MultipathFlush when map is not null => $"the multipath map {name} is still there",
            StorageActions.DrbdUp when resource is not { Up: true } => $"{name} isn't up afterwards",
            StorageActions.DrbdDown when resource is { Up: true } => $"{name} is still up",
            StorageActions.DrbdPrimary when resource?.Role != "Primary" => $"{name} is {resource?.Role ?? "down"}, not Primary",
            StorageActions.DrbdSecondary when resource?.Role != "Secondary" => $"{name} is {resource?.Role ?? "down"}, not Secondary",
            StorageActions.DrbdDisconnect when resource?.Peers.Any(p => p.ConnectionState != "StandAlone") == true => $"{name} is still connected to its peers",
            StorageActions.DrbdConnect when resource?.Peers.Any(p => p.ConnectionState == "StandAlone") == true => $"{name} is still disconnected (StandAlone)",
            StorageActions.LvmMount when lvm is not { Mounted: true } => $"{name} isn't mounted as Proxmox LVM storage on {after.Proxmox?.Node ?? "this node"}",
            StorageActions.LvmUnmount when lvm is { Mounted: true } => $"{name} is still mounted as storage {lvm.Storage}",
            _ => null,
        };
    }

    /// <summary>What a successful action changed, from the host's storage afterwards.</summary>
    internal static string Achieved(string action, string? target, string? portal, string? name, StorageStatus after)
    {
        var at = NormalizePortal(portal);
        var session = after.Iscsi?.Sessions.FirstOrDefault(s => s.Target == target && s.Portal == at);
        var resource = after.Drbd?.FirstOrDefault(r => r.Name == name);
        var lvm = after.Proxmox?.Maps.FirstOrDefault(m => m.Map == name);
        return action switch
        {
            StorageActions.LvmMount => $"Mounted {name} as Proxmox LVM storage {lvm?.Storage} (volume group {lvm?.VolumeGroup}) on {after.Proxmox?.Node}.",
            StorageActions.LvmUnmount => $"Proxmox no longer uses {name} on {after.Proxmox?.Node}{(lvm?.VolumeGroup is { } vg ? $"; volume group {vg} is deactivated here" : "")}.",
            StorageActions.IscsiLogin => $"Logged in to {target} at {at}{(session is { Disks.Count: > 0 } ? $"; its disks here: {string.Join(", ", session.Disks)}" : "; it has attached no disks (yet)")}.",
            StorageActions.IscsiLogout => $"Logged out of {target} at {at}.",
            StorageActions.IscsiRescan => "Rescanned the iSCSI sessions for new or resized LUNs.",
            StorageActions.MultipathReload => "Reloaded the multipath maps.",
            StorageActions.MultipathFlush => $"Removed the multipath map {name}.",
            StorageActions.DrbdUp or StorageActions.DrbdPrimary or StorageActions.DrbdSecondary or StorageActions.DrbdAdjust =>
                $"{name} is {(resource is { Up: true } ? resource.Role ?? "up" : "down")}{(resource is { Up: true } ? $", disk {string.Join("/", resource.Volumes.Select(v => v.DiskState ?? "unknown"))}" : "")}.",
            StorageActions.DrbdDown => $"{name} is down.",
            StorageActions.DrbdConnect or StorageActions.DrbdDisconnect =>
                $"{name}: {(resource is null || resource.Peers.Count == 0 ? "no peers" : string.Join(", ", resource.Peers.Select(p => $"{p.Name} {p.ConnectionState}")))}.",
            _ => "Done.",
        };
    }

    /// <summary>Runs a script as root (<c>$p</c> runs a command as root, the tools' folders are on the path); returns its output.</summary>
    /// <param name="what">What it does, for errors, e.g. "Mounting /srv/vms".</param>
    public static Task<string> RunAsRootAsync(SshClient client, string script, TimeSpan timeout, string what, CancellationToken cancellationToken) =>
        RunAsync(client, AsRoot + script, timeout, what, cancellationToken);

    /// <summary>
    /// The command for an action on the host as it is now (<paramref name="status"/>), or why it
    /// can't be done. <paramref name="name"/> is a multipath map or DRBD resource;
    /// <paramref name="storage"/> the ID for a new Proxmox storage when mounting a map that has none.
    /// </summary>
    public static StorageCommand CommandFor(string action, string? target, string? portal, string? name, StorageStatus status, string? storage = null)
    {
        switch (action)
        {
            case StorageActions.IscsiDiscover:
            {
                RequireIscsi(status);
                var address = NormalizePortal(portal) is { } p && PortalFormat().IsMatch(p)
                    ? p
                    : throw Refused("Enter the portal's address, e.g. 10.0.0.5 or 10.0.0.5:3260.");
                return new($"$p iscsiadm -m discovery -t sendtargets -p '{address}'", $"iSCSI discovery · {address}");
            }

            case StorageActions.IscsiLogin:
            {
                var node = RequireIscsi(status).Nodes.FirstOrDefault(n => n.Target == target && n.Portal == NormalizePortal(portal))
                    ?? throw Refused("This host doesn't know that iSCSI target. Discover it first.");
                if (node.LoggedIn)
                {
                    throw Refused($"{node.Target} is already logged in through {node.Portal}.");
                }

                return new(IscsiNodeCommand(node.Target, node.Portal, "--login"), $"iSCSI login · {node.Target} · {node.Portal}");
            }

            case StorageActions.IscsiLogout:
            {
                var session = RequireIscsi(status).Sessions.FirstOrDefault(s => s.Target == target && s.Portal == NormalizePortal(portal))
                    ?? throw Refused("There's no iSCSI session to that target any more.");

                // Its last path to a map Proxmox uses here would leave the storage without its disk.
                var others = status.Iscsi!.Sessions.Where(s => s != session && s.State == "LOGGED_IN").SelectMany(s => s.Disks).ToHashSet();
                if (MountedMaps(status).FirstOrDefault(m => m.Paths.Any(p => session.Disks.Contains(p.Device)) && !m.Paths.Any(p => others.Contains(p.Device))) is { } mounted)
                {
                    throw Refused($"{mounted.Name} is on this session and mounted as Proxmox storage {LvmFor(status, mounted.Name)!.Storage}. Unmount it first.");
                }

                return new(IscsiNodeCommand(session.Target, session.Portal, "--logout"), $"iSCSI logout · {session.Target} · {session.Portal}");
            }

            case StorageActions.IscsiRescan:
                return RequireIscsi(status).Sessions.Count == 0
                    ? throw Refused("There are no iSCSI sessions to rescan.")
                    : new("$p iscsiadm -m session --rescan", "iSCSI rescan");

            case StorageActions.MultipathReload:
                RequireMultipath(status);
                return new("$p multipath -r", "Multipath reload");

            case StorageActions.MultipathFlush:
            {
                var map = RequireMultipath(status).FirstOrDefault(m => m.Name == name) ?? throw Refused("There's no multipath map by that name any more.");
                if (LvmFor(status, map.Name) is { Mounted: true } lvm)
                {
                    throw Refused($"{map.Name} is mounted as Proxmox storage {lvm.Storage}. Unmount it first.");
                }

                return new($"$p multipath -f '{Checked(map.Name, MapName())}'", $"Multipath flush · {map.Name}");
            }

            case StorageActions.LvmMount:
                return MountCommand(RequireProxmox(status), name, storage?.Trim());

            case StorageActions.LvmUnmount:
                return UnmountCommand(RequireProxmox(status), name);

            case StorageActions.DrbdUp or StorageActions.DrbdDown or StorageActions.DrbdPrimary or StorageActions.DrbdSecondary
                or StorageActions.DrbdConnect or StorageActions.DrbdDisconnect or StorageActions.DrbdAdjust:
            {
                var resource = (status.Drbd ?? throw Refused("DRBD isn't installed on this host (drbdadm)."))
                    .FirstOrDefault(r => r.Name == name) ?? throw Refused("There's no DRBD resource by that name in this host's configuration.");
                var verb = action["drbd.".Length..];
                return new($"$p drbdadm {verb} '{Checked(resource.Name, MapName())}'", $"DRBD {verb} · {resource.Name}");
            }

            default:
                throw Refused("Choose an action.");
        }
    }

    /// <summary>Parses <see cref="StatusScript"/>'s output.</summary>
    internal static StorageStatus ParseStatus(string output)
    {
        var sections = Sections(output);
        if (sections.ContainsKey("noroot"))
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, RootMessage);
        }

        IscsiStatus? iscsi = null;
        if (sections.TryGetValue("iscsi-initiator", out var initiator))
        {
            var sessions = ParseIscsiSessions(sections.GetValueOrDefault("iscsi-sessions") ?? []);
            iscsi = new IscsiStatus(
                initiator.FirstOrDefault(l => l.Length > 0),
                sessions,
                ParseIscsiNodes(sections.GetValueOrDefault("iscsi-nodes") ?? [], sessions));
        }

        var multipath = sections.TryGetValue("multipath", out var maps) ? ParseMultipath(maps) : null;

        List<DrbdResource>? drbd = null;
        if (sections.TryGetValue("drbd-resources", out var configured))
        {
            var running = WithMinors(
                sections.TryGetValue("drbd-text", out var text) ? ParseDrbdText(text) : ParseDrbdJson(string.Join('\n', sections.GetValueOrDefault("drbd-json") ?? [])),
                sections.GetValueOrDefault("drbd-devices") ?? []);
            var names = configured.SelectMany(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(n => MapName().IsMatch(n));

            // Resources that are configured but not up are listed as down, so they can be brought up.
            drbd = [.. running
                .Concat(names.Where(n => running.All(r => r.Name != n)).Select(n => new DrbdResource(n, false, null, [], [])))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }

        ProxmoxLvm? proxmox = null;
        if (multipath is not null && sections.TryGetValue("pve-node", out var node) && node.Select(l => l.Trim()).FirstOrDefault(l => MapName().IsMatch(l)) is { } name)
        {
            proxmox = ParseProxmox(
                name,
                sections.GetValueOrDefault("pve-nodes") ?? [],
                sections.GetValueOrDefault("pve-storage") ?? [],
                sections.GetValueOrDefault("lvm-pvs") ?? [],
                sections.GetValueOrDefault("signatures") ?? [],
                multipath);
        }

        return new StorageStatus(iscsi, multipath, drbd, proxmox);
    }

    /// <summary>
    /// A Proxmox node's LVM storage on its multipath maps, from the cluster's nodes (<c>ls
    /// /etc/pve/nodes</c>), <c>/etc/pve/storage.cfg</c>, LVM's physical volumes (<c>name|resolved|vg</c>
    /// per line) and <c>lsblk -P</c>'s signatures. A map's physical volume may be named by its alias,
    /// its WWID or its device-mapper device.
    /// </summary>
    internal static ProxmoxLvm ParseProxmox(
        string node, IEnumerable<string> clusterNodes, IEnumerable<string> storageCfg, IEnumerable<string> pvs, IEnumerable<string> signatures, IReadOnlyList<MultipathMap> maps)
    {
        var cluster = clusterNodes.Select(l => l.Trim()).Where(n => MapName().IsMatch(n)).Append(node).Distinct().Order(StringComparer.Ordinal).ToList();
        var (all, storages) = ParseStorageCfg(storageCfg);

        var volumes = pvs
            .Select(l => l.Split('|'))
            .Where(f => f.Length == 3)
            .Select(f => (Name: f[0].Trim(), Resolved: f[1].Trim(), Vg: f[2].Trim()))
            .ToList();
        var found = signatures
            .Select(l => LsblkField().Matches(l).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal))
            .Where(f => f.ContainsKey("KNAME"))
            .GroupBy(f => f["KNAME"], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return new ProxmoxLvm(node, cluster, all, storages, [.. maps.Select(map =>
        {
            var dm = $"/dev/{map.DmDevice}";
            var volume = volumes.FirstOrDefault(v => v.Name == $"/dev/mapper/{map.Name}" || v.Name == $"/dev/mapper/{map.Wwid}" || v.Name == dm || v.Resolved == dm);
            var vg = volume.Vg is { Length: > 0 } ? volume.Vg : null;
            var fields = found.GetValueOrDefault(map.DmDevice);
            var fsType = fields?.GetValueOrDefault("FSTYPE") ?? "";
            var ptType = fields?.GetValueOrDefault("PTTYPE") ?? "";
            var signature = vg is not null ? null
                : volume.Name is not null || fsType == "LVM2_member" ? "an LVM physical volume without a volume group"
                : fsType.Length > 0 ? fsType
                : ptType.Length > 0 ? $"a {ptType} partition table"
                : null;
            var storage = vg is null ? null : storages.FirstOrDefault(s => s.VolumeGroup == vg);
            return new LvmMap(map.Name, vg, signature, storage?.Id, storage is { Disabled: false } && (storage.Nodes.Count == 0 || storage.Nodes.Contains(node)));
        })]);
    }

    /// <summary>
    /// <c>/etc/pve/storage.cfg</c>: a <c>type: id</c> line per storage, then its properties indented
    /// (<c>vgname vg</c>, <c>nodes a,b</c>, a bare <c>disable</c>…). Every storage's ID, and the LVM ones.
    /// </summary>
    internal static (List<string> All, List<ProxmoxLvmStorage> Lvm) ParseStorageCfg(IEnumerable<string> lines)
    {
        var all = new List<string>();
        var lvm = new List<ProxmoxLvmStorage>();
        (string Type, string Id, Dictionary<string, string> Properties)? current = null;

        void Finish()
        {
            if (current is { Type: "lvm" } s && s.Properties.TryGetValue("vgname", out var vg))
            {
                var nodes = s.Properties.GetValueOrDefault("nodes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                lvm.Add(new ProxmoxLvmStorage(s.Id, vg, nodes, s.Properties.GetValueOrDefault("disable") is { } d && d != "0"));
            }

            current = null;
        }

        foreach (var line in lines)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && StorageCfgHeader().Match(line.Trim()) is { Success: true } header)
            {
                Finish();
                current = (header.Groups["type"].Value, header.Groups["id"].Value, new Dictionary<string, string>(StringComparer.Ordinal));
                all.Add(current.Value.Id);
            }
            else if (current is { } s && line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } property)
            {
                s.Properties[property[0]] = property.Length > 1 ? property[1].Trim() : "1";
            }
        }

        Finish();
        return (all, lvm);
    }

    /// <summary>
    /// <c>iscsiadm -m session -P 3</c>: a <c>Target:</c> line, then for each of its sessions a
    /// <c>Current Portal:</c> line (where it's connected), a <c>Persistent Portal:</c> line (where it
    /// was logged in), the session's state, and its attached disks. Sessions are known by their
    /// persistent portal, which a SAN that redirects logins keeps while the current one changes.
    /// </summary>
    internal static List<IscsiSession> ParseIscsiSessions(IEnumerable<string> lines)
    {
        var sessions = new List<IscsiSession>();
        string? target = null;
        (string Connected, string Portal, string State, List<string> Disks)? current = null;

        void Finish()
        {
            if (target is not null && current is { } session)
            {
                sessions.Add(new IscsiSession(target, session.Portal, session.State, session.Disks, session.Connected == session.Portal ? [] : [session.Connected]));
            }

            current = null;
        }

        foreach (var line in lines.Select(l => l.Trim()))
        {
            if (line.StartsWith("Target:", StringComparison.Ordinal))
            {
                Finish();
                var name = line["Target:".Length..].Trim().Split(' ')[0];
                target = ShownName().IsMatch(name) ? name : null;
            }
            else if (line.StartsWith("Current Portal:", StringComparison.Ordinal))
            {
                Finish();
                var connected = NormalizePortal(line["Current Portal:".Length..].Trim()) ?? "";
                current = (connected, connected, "unknown", []);
            }
            else if (current is { } logged && line.StartsWith("Persistent Portal:", StringComparison.Ordinal))
            {
                current = logged with { Portal = NormalizePortal(line["Persistent Portal:".Length..].Trim()) ?? logged.Portal };
            }
            else if (current is { } session && line.StartsWith("iSCSI Session State:", StringComparison.Ordinal))
            {
                current = session with { State = line["iSCSI Session State:".Length..].Trim() };
            }
            else if (current is { } attached && AttachedDisk().Match(line) is { Success: true } disk)
            {
                attached.Disks.Add(disk.Groups["disk"].Value);
            }
        }

        Finish();

        // One per target and portal: a redirected login can open a session to each data address.
        return [.. sessions
            .GroupBy(s => (s.Target, s.Portal))
            .Select(g => new IscsiSession(
                g.Key.Target,
                g.Key.Portal,
                g.Any(s => s.State == "LOGGED_IN") ? "LOGGED_IN" : g.First().State,
                [.. g.SelectMany(s => s.Disks).Distinct()],
                [.. g.SelectMany(s => s.ConnectedTo ?? []).Distinct()]))];
    }

    /// <summary><c>iscsiadm -m node</c>: <c>portal,group target</c> per line.</summary>
    internal static List<IscsiNode> ParseIscsiNodes(IEnumerable<string> lines, IReadOnlyList<IscsiSession> sessions) =>
        [.. lines
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && ShownName().IsMatch(f[1]) && NormalizePortal(f[0]) is { } p && ShownName().IsMatch(p))
            .Select(f => (Target: f[1], Portal: NormalizePortal(f[0])!))
            .Distinct()
            .Select(n => new IscsiNode(n.Target, n.Portal, sessions.Any(s => s.Target == n.Target && s.Portal == n.Portal)))
            .OrderBy(n => n.Target, StringComparer.Ordinal)
            .ThenBy(n => n.Portal, StringComparer.Ordinal)];

    /// <summary>
    /// <c>multipath -ll</c>: per map, a header (<c>alias (wwid) dm-N vendor,product</c>, or
    /// <c>wwid dm-N vendor,product</c>), a <c>size=</c> line, then path groups and their paths
    /// (<c>H:C:T:L dev maj:min dm-state path-state online-state</c>).
    /// </summary>
    internal static List<MultipathMap> ParseMultipath(IEnumerable<string> lines)
    {
        var maps = new List<MultipathMap>();
        foreach (var line in lines)
        {
            if (MultipathHeader().Match(line) is { Success: true } header)
            {
                var name = header.Groups["name"].Value;
                var wwid = header.Groups["wwid"].Success ? header.Groups["wwid"].Value : name;
                var product = header.Groups["product"].Value.Trim();
                maps.Add(new MultipathMap(name, wwid, header.Groups["dm"].Value, product.Length > 0 ? product : null, null, []));
            }
            else if (maps.Count > 0 && MultipathSize().Match(line) is { Success: true } size && maps[^1].Size is null)
            {
                maps[^1] = maps[^1] with { Size = size.Groups["size"].Value };
            }
            else if (maps.Count > 0 && MultipathPathLine().Match(line) is { Success: true } path)
            {
                maps[^1] = maps[^1] with
                {
                    Paths = [.. maps[^1].Paths, new MultipathPath(
                        path.Groups["address"].Value, path.Groups["dev"].Value, path.Groups["dm"].Value, path.Groups["path"].Value, path.Groups["online"].Value)],
                };
            }
        }

        return [.. maps.Where(m => MapName().IsMatch(m.Name)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Fills in the minors status didn't give (DRBD 8.4 prints none) from the configured devices
    /// (<c>resource /dev/drbdN</c> lines, in volume order): the n-th volume is the n-th device.
    /// </summary>
    internal static List<DrbdResource> WithMinors(List<DrbdResource> resources, IEnumerable<string> devices)
    {
        var configured = devices
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && f[1].StartsWith("/dev/drbd", StringComparison.Ordinal) && int.TryParse(f[1]["/dev/drbd".Length..], CultureInfo.InvariantCulture, out _))
            .GroupBy(f => f[0], f => int.Parse(f[1]["/dev/drbd".Length..], CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. resources.Select(r => configured.TryGetValue(r.Name, out var minors)
            ? r with { Volumes = [.. r.Volumes.Select((v, i) => v.Minor is null && i < minors.Count ? v with { Minor = minors[i] } : v)] }
            : r)];
    }

    /// <summary><c>drbdsetup status --json</c> (DRBD 9's utilities).</summary>
    internal static List<DrbdResource> ParseDrbdJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "Couldn't read DRBD's status.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. document.RootElement.EnumerateArray()
                .Where(r => Text(r, "name") is { } n && MapName().IsMatch(n))
                .Select(r => new DrbdResource(
                    Text(r, "name")!,
                    true,
                    Text(r, "role"),
                    [.. Items(r, "devices").Select(d => new DrbdVolume(Integer(d, "volume") ?? 0, Integer(d, "minor"), Text(d, "disk-state")))],
                    [.. Items(r, "connections").Select(c => new DrbdPeer(
                        Text(c, "name") ?? $"node {Integer(c, "peer-node-id")}",
                        Text(c, "connection-state") ?? "Unknown",
                        Text(c, "peer-role"),
                        [.. Items(c, "peer_devices").Concat(Items(c, "peer-devices")).Select(v => new DrbdPeerVolume(
                            Integer(v, "volume") ?? 0, Text(v, "replication-state"), Text(v, "peer-disk-state"), Decimal(v, "percent-in-sync")))]))]))];
        }
    }

    /// <summary>
    /// <c>drbdsetup status --verbose</c>, for utilities without JSON: a resource line, then indented
    /// by two its volumes (<c>volume:0 minor:0 disk:UpToDate</c>) and peers (<c>name connection:… role:…</c>),
    /// and by four each peer's volumes (<c>volume:0 replication:… peer-disk:… done:12.50</c>).
    /// </summary>
    internal static List<DrbdResource> ParseDrbdText(IEnumerable<string> lines)
    {
        var resources = new List<DrbdResource>();
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var indent = line.Length - line.TrimStart().Length;
            var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = fields.Select(f => f.Split(':', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            if (indent == 0)
            {
                if (MapName().IsMatch(fields[0]))
                {
                    resources.Add(new DrbdResource(fields[0], true, values.GetValueOrDefault("role"), [], []));
                }

                continue;
            }

            if (resources.Count == 0)
            {
                continue;
            }

            var resource = resources[^1];
            var volume = int.TryParse(values.GetValueOrDefault("volume"), CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (indent <= 2 && (values.ContainsKey("disk") || values.ContainsKey("volume")))
            {
                var minor = int.TryParse(values.GetValueOrDefault("minor"), CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
                resources[^1] = resource with { Volumes = [.. resource.Volumes, new DrbdVolume(volume, minor, values.GetValueOrDefault("disk"))] };
            }
            else if (indent <= 2)
            {
                resources[^1] = resource with
                {
                    Peers = [.. resource.Peers, new DrbdPeer(fields[0], values.GetValueOrDefault("connection") ?? "Unknown", values.GetValueOrDefault("role"), [])],
                };
            }
            else if (resource.Peers.Count > 0)
            {
                var done = double.TryParse(values.GetValueOrDefault("done"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                var peer = resource.Peers[^1];
                peer = peer with
                {
                    Volumes = [.. peer.Volumes, new DrbdPeerVolume(volume, values.GetValueOrDefault("replication"), values.GetValueOrDefault("peer-disk"), done)],
                };
                resources[^1] = resource with { Peers = [.. resource.Peers.SkipLast(1), peer] };
            }
        }

        return resources;
    }

    /// <summary>A portal as iscsiadm takes it: the address and port, without the portal group (<c>,1</c>).</summary>
    internal static string? NormalizePortal(string? portal)
    {
        var trimmed = portal?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var comma = trimmed.LastIndexOf(',');
        return comma > 0 ? trimmed[..comma] : trimmed;
    }

    /// <summary>Why the host refused <paramref name="what"/> (e.g. "iSCSI login · …"): its tool's first line of complaint, or what NKeeper needs.</summary>
    internal static string RefusedMessage(string what, string error, int? exitStatus) =>
        error.Contains("sudo", StringComparison.OrdinalIgnoreCase) && error.Contains("password", StringComparison.OrdinalIgnoreCase)
            ? RootMessage
            : $"{what} failed: {(error.Length > 0 ? error.Split('\n')[0].Trim() : $"exit code {exitStatus}")}.";

    private const string RootMessage =
        "Reading and managing iSCSI, multipath and DRBD needs root: sign in as root, or as a user who may use sudo without a password.";

    private static string IscsiNodeCommand(string target, string portal, string verb) =>
        $"$p iscsiadm -m node -T '{Checked(target, Iqn())}' -p '{Checked(portal, PortalFormat())}' {verb}";

    /// <summary>
    /// Mounts a multipath map as Proxmox LVM storage on this node: adds the node to the storage on
    /// its volume group, or adds a storage for the volume group, or on a blank LUN creates the
    /// volume group first. A new storage is shared (each node logged in to the LUN can use it) and
    /// restricted to the nodes that mounted it.
    /// </summary>
    private static StorageCommand MountCommand(ProxmoxLvm pve, string? name, string? storage)
    {
        var map = pve.Maps.FirstOrDefault(m => m.Map == name) ?? throw Refused("There's no multipath map by that name any more.");
        if (map.Mounted)
        {
            throw Refused($"{map.Map} is already mounted as Proxmox storage {map.Storage}.");
        }

        var node = Checked(pve.Node, MapName());
        var what = $"Proxmox LVM mount · {map.Map} · ";
        if (map.Storage is not null)
        {
            var existing = pve.Storages.First(s => s.Id == map.Storage);
            if (existing.Disabled)
            {
                throw Refused($"Storage {existing.Id} is disabled for the whole cluster. Enable it in Datacenter ▸ Storage first.");
            }

            var nodes = string.Join(',', existing.Nodes.Append(pve.Node).Select(n => Checked(n, MapName())));
            return new($"$p pvesm set '{Checked(existing.Id, StorageId())}' --nodes '{nodes}'", $"{what}{existing.Id} on {pve.Node}");
        }

        if (map.VolumeGroup is null && map.Signature is not null)
        {
            throw Refused($"{map.Map} has {map.Signature} on it. NKeeper sets up LVM only on a blank LUN, or one that's already an LVM volume group.");
        }

        var id = storage is { Length: > 0 } ? storage : throw Refused("Name the new Proxmox storage.");
        if (!StorageId().IsMatch(id))
        {
            throw Refused("A storage ID starts with a letter, ends with a letter or digit, and has only letters, digits, '-', '_' and '.' between.");
        }

        if (pve.AllStorage.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            throw Refused($"There's already a storage named {id}.");
        }

        string Add(string vg) => $"$p pvesm add lvm '{id}' --vgname '{Checked(vg, VgName())}' --content rootdir,images --shared 1 --nodes '{node}'";
        if (map.VolumeGroup is { } group)
        {
            return new(Add(group), $"{what}{id} (volume group {group}) on {pve.Node}");
        }

        // A blank LUN, checked again on the host itself (blkid exits 2 when it finds nothing).
        var device = $"/dev/mapper/{Checked(map.Map, MapName())}";
        return new(
            $"$p blkid -p '{device}' >/dev/null 2>&1; [ $? = 2 ] || {{ echo '{device} is not blank: blkid found a signature on it' >&2; exit 1; }}; "
            + $"$p pvcreate '{device}' && $p vgcreate '{id}' '{device}' && {Add(id)}",
            $"{what}{id} (new volume group {id}) on {pve.Node}");
    }

    /// <summary>
    /// Stops Proxmox using a map's LVM storage on this node, unless a guest here has disks on it or
    /// one of its volumes is open: takes the node off the storage (removing the storage when no
    /// node is left; the volume group and its data stay), then deactivates the volume group here.
    /// </summary>
    private static StorageCommand UnmountCommand(ProxmoxLvm pve, string? name)
    {
        var map = pve.Maps.FirstOrDefault(m => m.Map == name) ?? throw Refused("There's no multipath map by that name any more.");
        if (!map.Mounted)
        {
            throw Refused($"{map.Map} isn't mounted as Proxmox storage on {pve.Node}.");
        }

        var existing = pve.Storages.First(s => s.Id == map.Storage);
        var id = Checked(existing.Id, StorageId());
        var vg = Checked(existing.VolumeGroup, VgName());
        var node = Checked(pve.Node, MapName());
        var others = (existing.Nodes.Count > 0 ? existing.Nodes : pve.ClusterNodes).Where(n => n != pve.Node).Select(n => Checked(n, MapName())).ToList();
        var release = others.Count > 0 ? $"$p pvesm set '{id}' --nodes '{string.Join(',', others)}'" : $"$p pvesm remove '{id}'";
        return new(
            $"used=$($p grep -rlsE '^[a-z0-9]+: *{id}:' /etc/pve/nodes/{node}/qemu-server /etc/pve/nodes/{node}/lxc | sed 's|.*/||; s|[.]conf$||'); "
            + $"[ -z \"$used\" ] || {{ echo \"Guests $(echo $used) on {node} have disks on storage {id}; move them off it first\" >&2; exit 1; }}; "
            + $"open=$($p lvs --noheadings -o lv_name,lv_attr '{vg}' 2>/dev/null | awk 'substr($2,6,1)==\"o\" {{print $1}}'); "
            + $"[ -z \"$open\" ] || {{ echo \"Volumes $(echo $open) of {vg} are open on {node}\" >&2; exit 1; }}; "
            + $"{release} && $p vgchange -an '{vg}'",
            $"Proxmox LVM unmount · {map.Map} · {existing.Id} on {pve.Node}");
    }

    private static LvmMap? LvmFor(StorageStatus status, string name) => status.Proxmox?.Maps.FirstOrDefault(m => m.Map == name);

    /// <summary>The multipath maps Proxmox uses on this node.</summary>
    private static IEnumerable<MultipathMap> MountedMaps(StorageStatus status) =>
        (status.Multipath ?? []).Where(m => LvmFor(status, m.Name) is { Mounted: true });

    private static ProxmoxLvm RequireProxmox(StorageStatus status) =>
        status.Proxmox ?? throw Refused("This host isn't a Proxmox node with multipath (pvesm, multipath).");

    private static IscsiStatus RequireIscsi(StorageStatus status) =>
        status.Iscsi ?? throw Refused("iSCSI isn't installed on this host (iscsiadm, from open-iscsi or iscsi-initiator-utils).");

    private static IReadOnlyList<MultipathMap> RequireMultipath(StorageStatus status) =>
        status.Multipath ?? throw Refused("Multipath isn't installed on this host (multipath-tools or device-mapper-multipath).");

    /// <summary>A value that goes in a command line in single quotes, which these formats never contain.</summary>
    private static string Checked(string value, Regex format) =>
        format.IsMatch(value) ? value : throw Refused("That name can't be used safely in a command.");

    private static SessionException Refused(string message) => new(SessionErrorCodes.BadRequest, message);

    /// <param name="withErrors">Return what it wrote to stderr too (after its output), e.g. iscsiadm's messages.</param>
    private static async Task<string> RunAsync(SshClient client, string script, TimeSpan timeout, string what, CancellationToken cancellationToken, bool withErrors = false)
    {
        using var command = client.CreateCommand(script);
        command.CommandTimeout = timeout;
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"{what} didn't finish in time.");
        }

        return command.ExitStatus is 0
            ? withErrors ? $"{command.Result}\n{command.Error}" : command.Result
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, RefusedMessage(what, command.Error.Trim(), command.ExitStatus));
    }

    /// <summary>Splits the output at its <c>@@name</c> lines.</summary>
    private static Dictionary<string, List<string>> Sections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..].Trim()] = [];
            }
            else
            {
                current?.Add(line);
            }
        }

        return sections;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static int? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? Decimal(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static JsonElement[] Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];

    /// <summary>An iSCSI name: iqn.yyyy-mm.reversed.domain[:anything], eui.(16 hex) or naa.(16 or 32 hex).</summary>
    [GeneratedRegex(@"^(?:iqn\.\d{4}-\d{2}\.[A-Za-z0-9][A-Za-z0-9.-]*(?::[A-Za-z0-9.:_-]+)?|eui\.[0-9A-Fa-f]{16}|naa\.[0-9A-Fa-f]{16}(?:[0-9A-Fa-f]{16})?)$")]
    private static partial Regex Iqn();

    /// <summary>
    /// A target or portal name as iscsiadm reports it, for showing: anything without spaces, quotes
    /// or backslashes. Commands still accept only <see cref="Iqn"/> and <see cref="PortalFormat"/>,
    /// so a name they can't use is refused with a message instead of hidden.
    /// </summary>
    [GeneratedRegex(@"^[^\s'""\\]{1,255}$")]
    private static partial Regex ShownName();

    /// <summary>A portal: a host name, IPv4 address or [IPv6 address], and optionally a port.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9.-]{0,252}|\[[0-9A-Fa-f:.]{2,45}\])(?::\d{1,5})?$")]
    private static partial Regex PortalFormat();

    /// <summary>A multipath map's alias or WWID, or a DRBD resource's name.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:+-]{0,127}$")]
    private static partial Regex MapName();

    /// <summary>A Proxmox storage ID.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,62}[A-Za-z0-9]$")]
    private static partial Regex StorageId();

    /// <summary>An LVM volume group's name.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9+_.][A-Za-z0-9+_.-]{0,126}$")]
    private static partial Regex VgName();

    [GeneratedRegex(@"^(?<type>[a-z][a-z0-9]*):\s*(?<id>\S+)$")]
    private static partial Regex StorageCfgHeader();

    [GeneratedRegex(@"(?<key>[A-Z]+)=""(?<value>[^""]*)""")]
    private static partial Regex LsblkField();

    [GeneratedRegex(@"Attached scsi disk (?<disk>[A-Za-z0-9]+)")]
    private static partial Regex AttachedDisk();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][^\s()]*)\s+(?:\((?<wwid>[^)\s]+)\)\s+)?(?<dm>dm-\d+)\s*(?<product>.*)$")]
    private static partial Regex MultipathHeader();

    [GeneratedRegex(@"^size=(?<size>\S+)")]
    private static partial Regex MultipathSize();

    [GeneratedRegex(@"(?<address>\d+:\d+:\d+:\d+)\s+(?<dev>\S+)\s+\d+:\d+\s+(?<dm>\S+)\s+(?<path>\S+)\s+(?<online>\S+)")]
    private static partial Regex MultipathPathLine();
}
