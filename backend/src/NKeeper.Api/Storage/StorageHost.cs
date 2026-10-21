using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Storage;

/// <summary>An iSCSI session: the target, the portal it's logged in through, its state and the disks it attached.</summary>
/// <param name="Portal">The portal's address and port, e.g. <c>10.0.0.5:3260</c> (without the portal group).</param>
/// <param name="State">The session state, e.g. <c>LOGGED_IN</c>, <c>FAILED</c> or <c>FREE</c>.</param>
/// <param name="Disks">The SCSI disks it attached, e.g. <c>sdb</c>.</param>
public sealed record IscsiSession(string Target, string Portal, string State, IReadOnlyList<string> Disks);

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

/// <summary>What a host's shared storage looks like; each part is null when its tools aren't installed.</summary>
public sealed record StorageStatus(IscsiStatus? Iscsi, IReadOnlyList<MultipathMap>? Multipath, IReadOnlyList<DrbdResource>? Drbd);

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
    private const string AsRoot = "PATH=$PATH:/usr/sbin:/sbin; p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; ";

    /// <summary>
    /// Prints each installed tool's state after an <c>@@section</c> line, or only <c>@@noroot</c> when
    /// a tool is installed but the user can't become root.
    /// </summary>
    public const string StatusScript =
        AsRoot
        + "have() { command -v \"$1\" >/dev/null 2>&1; }; "
        + "if [ -n \"$p\" ] && { have iscsiadm || have multipath || have drbdadm; } && ! sudo -n true 2>/dev/null; then echo '@@noroot'; exit 0; fi; "
        + "if have iscsiadm; then echo '@@iscsi-initiator'; $p sed -n 's/^InitiatorName=//p' /etc/iscsi/initiatorname.iscsi 2>/dev/null; "
        + "echo '@@iscsi-sessions'; $p iscsiadm -m session -P 3 2>/dev/null; echo '@@iscsi-nodes'; $p iscsiadm -m node 2>/dev/null; fi; "
        + "if have multipath; then echo '@@multipath'; $p multipath -ll 2>/dev/null; fi; "
        + "if have drbdadm; then echo '@@drbd-resources'; $p drbdadm sh-resources 2>/dev/null; "
        + "echo '@@drbd-json'; $p drbdsetup status --json 2>/dev/null || { echo '@@drbd-text'; $p drbdsetup status --verbose 2>/dev/null; }; fi; "
        + "exit 0";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>iSCSI logins and discovery wait for the target; give them longer than iscsiadm's own timeouts.</summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(150);

    /// <summary>The host's storage now.</summary>
    public static async Task<StorageStatus> GetStatusAsync(SshClient client, CancellationToken cancellationToken) =>
        ParseStatus(await RunAsync(client, StatusScript, StatusTimeout, "Reading the host's storage", cancellationToken));

    /// <summary>Runs an action's command; returns what it printed.</summary>
    public static Task<string> RunActionAsync(SshClient client, StorageCommand command, CancellationToken cancellationToken) =>
        RunAsync(client, AsRoot + command.Script, ActionTimeout, command.Description, cancellationToken);

    /// <summary>
    /// The command for an action on the host as it is now (<paramref name="status"/>), or why it
    /// can't be done. <paramref name="name"/> is a multipath map or DRBD resource.
    /// </summary>
    public static StorageCommand CommandFor(string action, string? target, string? portal, string? name, StorageStatus status)
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
                return new($"$p multipath -f '{Checked(map.Name, MapName())}'", $"Multipath flush · {map.Name}");
            }

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
            var running = sections.TryGetValue("drbd-text", out var text)
                ? ParseDrbdText(text)
                : ParseDrbdJson(string.Join('\n', sections.GetValueOrDefault("drbd-json") ?? []));
            var names = configured.SelectMany(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(n => MapName().IsMatch(n));

            // Resources that are configured but not up are listed as down, so they can be brought up.
            drbd = [.. running
                .Concat(names.Where(n => running.All(r => r.Name != n)).Select(n => new DrbdResource(n, false, null, [], [])))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }

        return new StorageStatus(iscsi, multipath, drbd);
    }

    /// <summary>
    /// <c>iscsiadm -m session -P 3</c>: a <c>Target:</c> line, then for each of its sessions a
    /// <c>Current Portal:</c> line, the session's state, and its attached disks.
    /// </summary>
    internal static List<IscsiSession> ParseIscsiSessions(IEnumerable<string> lines)
    {
        var sessions = new List<IscsiSession>();
        string? target = null;
        (string Portal, string State, List<string> Disks)? current = null;

        void Finish()
        {
            if (target is not null && current is { } session)
            {
                sessions.Add(new IscsiSession(target, session.Portal, session.State, session.Disks));
            }

            current = null;
        }

        foreach (var line in lines.Select(l => l.Trim()))
        {
            if (line.StartsWith("Target:", StringComparison.Ordinal))
            {
                Finish();
                var name = line["Target:".Length..].Trim().Split(' ')[0];
                target = Iqn().IsMatch(name) ? name : null;
            }
            else if (line.StartsWith("Current Portal:", StringComparison.Ordinal))
            {
                Finish();
                current = (NormalizePortal(line["Current Portal:".Length..].Trim()) ?? "", "unknown", []);
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
        return sessions;
    }

    /// <summary><c>iscsiadm -m node</c>: <c>portal,group target</c> per line.</summary>
    internal static List<IscsiNode> ParseIscsiNodes(IEnumerable<string> lines, IReadOnlyList<IscsiSession> sessions) =>
        [.. lines
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && Iqn().IsMatch(f[1]) && NormalizePortal(f[0]) is { } p && PortalFormat().IsMatch(p))
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

    private static IscsiStatus RequireIscsi(StorageStatus status) =>
        status.Iscsi ?? throw Refused("iSCSI isn't installed on this host (iscsiadm, from open-iscsi or iscsi-initiator-utils).");

    private static IReadOnlyList<MultipathMap> RequireMultipath(StorageStatus status) =>
        status.Multipath ?? throw Refused("Multipath isn't installed on this host (multipath-tools or device-mapper-multipath).");

    /// <summary>A value that goes in a command line in single quotes, which these formats never contain.</summary>
    private static string Checked(string value, Regex format) =>
        format.IsMatch(value) ? value : throw Refused("That name can't be used safely in a command.");

    private static SessionException Refused(string message) => new(SessionErrorCodes.BadRequest, message);

    private static async Task<string> RunAsync(SshClient client, string script, TimeSpan timeout, string what, CancellationToken cancellationToken)
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
            ? command.Result
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

    /// <summary>A portal: a host name, IPv4 address or [IPv6 address], and optionally a port.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9.-]{0,252}|\[[0-9A-Fa-f:.]{2,45}\])(?::\d{1,5})?$")]
    private static partial Regex PortalFormat();

    /// <summary>A multipath map's alias or WWID, or a DRBD resource's name.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:+-]{0,127}$")]
    private static partial Regex MapName();

    [GeneratedRegex(@"Attached scsi disk (?<disk>[A-Za-z0-9]+)")]
    private static partial Regex AttachedDisk();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][^\s()]*)\s+(?:\((?<wwid>[^)\s]+)\)\s+)?(?<dm>dm-\d+)\s*(?<product>.*)$")]
    private static partial Regex MultipathHeader();

    [GeneratedRegex(@"^size=(?<size>\S+)")]
    private static partial Regex MultipathSize();

    [GeneratedRegex(@"(?<address>\d+:\d+:\d+:\d+)\s+(?<dev>\S+)\s+\d+:\d+\s+(?<dm>\S+)\s+(?<path>\S+)\s+(?<online>\S+)")]
    private static partial Regex MultipathPathLine();
}
