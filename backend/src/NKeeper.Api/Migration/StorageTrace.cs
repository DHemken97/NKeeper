using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Storage;
using Renci.SshNet;

namespace NKeeper.Api.Migration;

/// <summary>
/// One layer a disk is built on, from the top down: an LVM volume (<c>vg/lv</c>), a DRBD resource
/// (detail: its minor), a multipath map (name: its WWID, detail: its alias), an iSCSI LUN (name: the
/// target, detail: the portal) or a local disk.
/// </summary>
public sealed record StorageLayer(string Kind, string Name, string? Detail = null);

public static class LayerKinds
{
    public const string Lvm = "lvm";
    public const string Drbd = "drbd";
    public const string Multipath = "multipath";
    public const string Iscsi = "iscsi";
    public const string Local = "local";
}

/// <summary>A mounted file system a disk's image file is on.</summary>
/// <param name="Device">The device mounted, e.g. <c>/dev/drbd0</c>.</param>
public sealed record StorageMount(string Device, string MountPoint, string FsType, string? Uuid);

/// <summary>How a disk's storage moves with the VM.</summary>
public static class DiskLayouts
{
    /// <summary>The hypervisor (Proxmox) activates it on the new host itself; NKeeper only makes sure its iSCSI LUNs are there.</summary>
    public const string Managed = "managed";

    /// <summary>A network file system or network disk every host reaches on its own (NFS, Ceph…).</summary>
    public const string Network = "network";

    /// <summary>A cluster file system (GFS2, OCFS2) mounted on every host at once.</summary>
    public const string Shared = "shared";

    /// <summary>An image file on a file system that is on shared storage: only one host may mount it, so the VM must stop.</summary>
    public const string Filesystem = "filesystem";

    /// <summary>A shared block device (DRBD, LVM, multipath, iSCSI) used directly as the disk.</summary>
    public const string Block = "block";

    /// <summary>On the old host's own disks.</summary>
    public const string Local = "local";
}

/// <summary>A VM disk and the storage under it.</summary>
/// <param name="Name">The disk as the VM knows it, e.g. <c>vda</c> or <c>scsi0</c>.</param>
/// <param name="Storage">The Proxmox storage it is on, if any.</param>
public sealed record GuestDisk(string Name, string Path, string Layout, StorageMount? Mount, IReadOnlyList<StorageLayer> Layers, string? Storage = null, string? Note = null);

/// <summary>A disk to trace: its name, and the path (file or block device) its data is at.</summary>
/// <param name="Handling">
/// How its storage is treated: "trace" (follow it down and move what it's on), "managed" (Proxmox
/// activates it on the new node; only its iSCSI LUNs are checked), "remote" (reached over the
/// network by every host) or "local" (the old host's own).
/// </param>
/// <param name="Shared">Proxmox: whether its storage is marked shared (then Proxmox doesn't copy it).</param>
internal sealed record DiskPath(string Name, string Path, string? Storage = null, string Handling = "trace", bool Shared = false, string? Note = null);

/// <summary>What tracing the old host's disks found: each disk, and which iSCSI LUNs something else uses.</summary>
/// <param name="IscsiInUse">iSCSI targets (target and portal) used by more than this VM's disks, with what uses them.</param>
internal sealed record TraceResult(IReadOnlyList<GuestDisk> Disks, IReadOnlyDictionary<(string Target, string Portal), string> IscsiInUse);

/// <summary>
/// Traces VM disks down through file systems, LVM, DRBD, multipath and iSCSI on a host, with
/// <c>findmnt</c>, <c>lsblk</c> and <c>lvs</c>, and joins the result with the host's storage status.
/// </summary>
internal static partial class StorageTrace
{
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smb3", "glusterfs", "fuse.glusterfs", "ceph", "fuse.ceph", "fuse.sshfs", "9p", "virtiofs",
    };

    private static readonly HashSet<string> ClusterFileSystems = new(StringComparer.Ordinal) { "gfs2", "ocfs2" };

    /// <summary>Traces <paramref name="disks"/> on the host <paramref name="client"/> is connected to.</summary>
    public static async Task<TraceResult> TraceAsync(SshClient client, IReadOnlyList<DiskPath> disks, StorageStatus status, CancellationToken cancellationToken) =>
        Parse(await StorageHost.RunAsRootAsync(client, Script(disks), TimeSpan.FromSeconds(30), "Tracing the VM's disks", cancellationToken), disks, status);

    /// <summary>
    /// For each disk, <c>@@disk</c>, then <c>@@block</c> and its device, or <c>@@mount</c> and
    /// <c>findmnt</c>'s JSON for the file's file system; then <c>@@tree</c> and <c>lsblk -s</c>'s
    /// JSON (what the device is built on). Then the LVM volumes, and what is built on each iSCSI disk.
    /// </summary>
    internal static string Script(IReadOnlyList<DiskPath> disks)
    {
        var script = new StringBuilder(
            "t() { if [ -b \"$1\" ]; then echo '@@block'; d=$(readlink -f \"$1\"); echo \"$d\"; "
            + "else echo '@@mount'; findmnt -n -J -o SOURCE,TARGET,FSTYPE,UUID --target \"$1\"; d=$(findmnt -n -o SOURCE --target \"$1\"); d=${d%%\\[*}; fi; "
            + "if [ -b \"$d\" ]; then echo '@@tree'; lsblk -s -J -o NAME,KNAME,TYPE,TRAN \"$d\"; fi; }; ");
        foreach (var disk in disks)
        {
            script.Append(CultureInfo.InvariantCulture, $"echo '@@disk'; t '{Quoted(disk.Path)}'; ");
        }

        script.Append("echo '@@lvs'; $p lvs --noheadings --separator '|' -o lv_dm_path,vg_name,lv_name 2>/dev/null; ");
        script.Append("for d in $(lsblk -dn -o KNAME,TRAN | awk '$2==\"iscsi\"{print $1}'); do echo \"@@users $d\"; lsblk -J -o NAME,KNAME,TYPE,MOUNTPOINT \"/dev/$d\"; done; exit 0");
        return script.ToString();
    }

    /// <summary>
    /// An image file on a file system traced down to the host's own disks, with no DRBD, multipath or
    /// iSCSI under it: the only kind of disk NKeeper copies. A file on shared storage moves with that
    /// storage, and one whose storage couldn't be traced might be on it.
    /// </summary>
    internal static bool IsLocalFile(GuestDisk disk) =>
        disk.Layout == DiskLayouts.Local && disk.Mount is not null
        && disk.Layers.Any(l => l.Kind == LayerKinds.Local)
        && !disk.Layers.Any(l => l.Kind is LayerKinds.Drbd or LayerKinds.Multipath or LayerKinds.Iscsi);

    /// <summary>A path that came from the host's own tools, checked so it can go in single quotes.</summary>
    internal static string Quoted(string path) =>
        SafePath().IsMatch(path) ? path : throw new SessionException(SessionErrorCodes.ConnectionFailed, $"NKeeper can't handle the disk path “{path}”.");

    internal static TraceResult Parse(string output, IReadOnlyList<DiskPath> disks, StorageStatus status)
    {
        var chunks = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var diskChunks = new List<List<string>>();
        var lvs = new Dictionary<string, string>(StringComparer.Ordinal);
        var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        var section = "";
        foreach (var line in chunks)
        {
            if (line == "@@disk")
            {
                current = [];
                diskChunks.Add(current);
                section = "disk";
            }
            else if (line == "@@lvs")
            {
                section = "lvs";
            }
            else if (line.StartsWith("@@users ", StringComparison.Ordinal))
            {
                section = line;
                users[line["@@users ".Length..]] = [];
            }
            else if (section == "disk")
            {
                current!.Add(line);
            }
            else if (section == "lvs" && line.Trim().Split('|') is [var path, var vg, var lv])
            {
                lvs[path.Trim()] = $"{vg.Trim()}/{lv.Trim()}";
            }
            else if (section.StartsWith("@@users ", StringComparison.Ordinal))
            {
                users[section["@@users ".Length..]].Add(line);
            }
        }

        var result = new List<GuestDisk>();
        var ours = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < disks.Count; i++)
        {
            var (disk, knames) = ParseDisk(disks[i], i < diskChunks.Count ? diskChunks[i] : [], lvs, status);
            result.Add(disk);
            ours.UnionWith(knames);
        }

        return new TraceResult(result, IscsiUsers(users, ours, status));
    }

    private static (GuestDisk Disk, HashSet<string> Knames) ParseDisk(DiskPath disk, List<string> lines, Dictionary<string, string> lvs, StorageStatus status)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..]] = [];
            }
            else
            {
                current?.Add(line);
            }
        }

        StorageMount? mount = null;
        if (sections.TryGetValue("mount", out var findmnt) && Json(string.Join('\n', findmnt)) is { } fs
            && fs.RootElement.TryGetProperty("filesystems", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            using (fs)
            {
                var f = list[0];
                var source = Text(f, "source") ?? "";
                var bracket = source.IndexOf('[', StringComparison.Ordinal);
                mount = new StorageMount(bracket > 0 ? source[..bracket] : source, Text(f, "target") ?? "/", Text(f, "fstype") ?? "", Text(f, "uuid"));
            }
        }

        var layers = new List<StorageLayer>();
        var knames = new HashSet<string>(StringComparer.Ordinal);
        if (sections.TryGetValue("tree", out var tree) && Json(string.Join('\n', tree)) is { } lsblk)
        {
            using (lsblk)
            {
                if (lsblk.RootElement.TryGetProperty("blockdevices", out var roots) && roots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var root in roots.EnumerateArray())
                    {
                        Walk(root, layers, knames, lvs, status);
                    }
                }
            }
        }

        if (mount is { } m && NetworkFileSystems.Contains(m.FsType))
        {
            return (new GuestDisk(disk.Name, disk.Path, DiskLayouts.Network, m, [], disk.Storage), knames);
        }

        if (mount is { } c && ClusterFileSystems.Contains(c.FsType))
        {
            return (new GuestDisk(disk.Name, disk.Path, DiskLayouts.Shared, c, layers, disk.Storage), knames);
        }

        var shared = layers.Any(l => l.Kind is LayerKinds.Drbd or LayerKinds.Multipath or LayerKinds.Iscsi);
        if (!sections.ContainsKey("tree"))
        {
            return (new GuestDisk(disk.Name, disk.Path, DiskLayouts.Local, mount, [], disk.Storage, "NKeeper couldn't tell what this is stored on."), knames);
        }

        var layout = !shared ? DiskLayouts.Local : mount is null ? DiskLayouts.Block : DiskLayouts.Filesystem;
        return (new GuestDisk(disk.Name, disk.Path, layout, mount, layers, disk.Storage), knames);
    }

    /// <summary>Walks <c>lsblk -s</c>'s tree (each device, then what it's built on), stopping at DRBD, whose backing disk is each host's own.</summary>
    private static void Walk(JsonElement device, List<StorageLayer> layers, HashSet<string> knames, Dictionary<string, string> lvs, StorageStatus status)
    {
        var name = Text(device, "name") ?? "";
        var kname = Text(device, "kname") ?? name;
        var type = Text(device, "type") ?? "";
        knames.Add(kname);
        StorageLayer? layer = null;
        var descend = true;
        if (kname.StartsWith("drbd", StringComparison.Ordinal) && int.TryParse(kname[4..], CultureInfo.InvariantCulture, out var minor))
        {
            var resource = status.Drbd?.FirstOrDefault(r => r.Volumes.Any(v => v.Minor == minor))?.Name ?? kname;
            layer = new StorageLayer(LayerKinds.Drbd, resource, minor.ToString(CultureInfo.InvariantCulture));
            descend = false;
        }
        else if (type == "lvm")
        {
            layer = new StorageLayer(LayerKinds.Lvm, lvs.GetValueOrDefault($"/dev/mapper/{name}") ?? name);
        }
        else if (type == "mpath" || status.Multipath?.Any(m => m.DmDevice == kname) == true)
        {
            var map = status.Multipath?.FirstOrDefault(m => m.Name == name || m.Wwid == name || m.DmDevice == kname);
            layer = new StorageLayer(LayerKinds.Multipath, map?.Wwid ?? name, map?.Name ?? name);
        }
        else if (type == "disk" && (Text(device, "tran") == "iscsi" || status.Iscsi?.Sessions.Any(s => s.Disks.Contains(kname)) == true))
        {
            // An iSCSI session's disk even when lsblk doesn't say so (older lsblk leaves TRAN empty).
            var session = status.Iscsi?.Sessions.FirstOrDefault(s => s.Disks.Contains(kname));
            layer = session is null ? new StorageLayer(LayerKinds.Iscsi, kname) : new StorageLayer(LayerKinds.Iscsi, session.Target, session.Portal);
        }
        else if (type is "disk" or "loop" or "rom")
        {
            layer = new StorageLayer(LayerKinds.Local, kname);
        }

        if (layer is not null && !layers.Contains(layer))
        {
            layers.Add(layer);
        }

        if (descend && device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Walk(child, layers, knames, lvs, status);
            }
        }
    }

    /// <summary>
    /// iSCSI targets whose disks carry something besides this VM's storage (another volume, a
    /// mount, a DRBD device), so logging out of them would take it away.
    /// </summary>
    private static Dictionary<(string Target, string Portal), string> IscsiUsers(Dictionary<string, List<string>> users, HashSet<string> ours, StorageStatus status)
    {
        var inUse = new Dictionary<(string, string), string>();
        foreach (var (disk, lines) in users)
        {
            var session = status.Iscsi?.Sessions.FirstOrDefault(s => s.Disks.Contains(disk));
            if (session is null || Json(string.Join('\n', lines)) is not { } tree)
            {
                continue;
            }

            using (tree)
            {
                var others = new List<string>();
                if (tree.RootElement.TryGetProperty("blockdevices", out var roots) && roots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var root in roots.EnumerateArray())
                    {
                        Others(root, ours, others, top: true);
                    }
                }

                if (others.Count > 0)
                {
                    inUse.TryAdd((session.Target, session.Portal), string.Join(", ", others.Distinct()));
                }
            }
        }

        return inUse;
    }

    private static void Others(JsonElement device, HashSet<string> ours, List<string> others, bool top)
    {
        var name = Text(device, "name") ?? "";
        var kname = Text(device, "kname") ?? name;
        var type = Text(device, "type") ?? "";
        var mounted = Text(device, "mountpoint");
        if (!top && !ours.Contains(kname) && (mounted is not null || type is "lvm" or "crypt" || kname.StartsWith("drbd", StringComparison.Ordinal) || type.StartsWith("raid", StringComparison.Ordinal)))
        {
            others.Add(mounted is null ? name : $"{name} ({mounted})");
        }

        if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Others(child, ours, others, top: false);
            }
        }
    }

    private static JsonDocument? Json(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>An absolute path without quotes, backslashes or control characters.</summary>
    [GeneratedRegex(@"^/[^'\\\x00-\x1f]*$")]
    private static partial Regex SafePath();
}
