using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using Renci.SshNet;
using NKeeper.Sdk.Consoles;

namespace NKeeper.Api.Kvm;

/// <summary>
/// A Proxmox VE node, over SSH: the QEMU virtual machines of its cluster (from <c>pvesh</c>) and
/// their VNC consoles through <c>qm vncproxy</c>, which relays a VM's display on stdin/stdout with
/// a one-time VNC password. That is how Proxmox's own web console reaches VMs, including those on
/// other nodes (over the cluster's SSH between nodes). Needs root, or sudo without a password.
/// </summary>
internal static partial class ProxmoxHost
{
    /// <summary>Shell test for a Proxmox node (the cluster file system).</summary>
    public const string Detect = "[ -d /etc/pve/nodes ]";

    /// <summary>The exit status of Proxmox scripts when <c>pvesh</c> or <c>qm</c> refuse.</summary>
    public const int RefusedExitStatus = 70;

    /// <summary>Sets <c>$p</c> to the prefix that runs a command as root.</summary>
    private const string AsRoot = "p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; ";

    /// <summary>
    /// Prints a "proxmox" line with this node's name, the cluster's VMs and containers as JSON, then
    /// each VM configuration's <c>vga:</c> lines (the current configuration's come first).
    /// </summary>
    public const string ListScript =
        AsRoot
        + "printf 'proxmox\\t%s\\n' \"$(uname -n | cut -d. -f1)\"; "
        + "$p pvesh get /cluster/resources --type vm --output-format json || exit 70; echo; "
        + "$p grep -H '^vga:' /etc/pve/nodes/*/qemu-server/*.conf 2>/dev/null; exit 0";

    /// <summary>
    /// Run on the target node with the VNC password as the first line of input: <c>qm vncproxy</c>
    /// sets it on the VM's display (valid for 30 seconds) and relays the display.
    /// </summary>
    private const string VncProxy = "read -r t && LC_PVE_TICKET=$t exec qm vncproxy ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The listing, or null when <paramref name="output"/> isn't from a Proxmox node.</summary>
    public static HypervisorListing? ParseListing(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !lines[0].StartsWith("proxmox\t", StringComparison.Ordinal))
        {
            return null;
        }

        var node = lines[0]["proxmox\t".Length..];
        List<Resource>? resources;
        try
        {
            resources = JsonSerializer.Deserialize<List<Resource>>(lines[1], Json);
        }
        catch (JsonException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "Couldn't read the Proxmox cluster's list of virtual machines.");
        }

        // The first vga line of each configuration is its current one (snapshots follow).
        var vga = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var match in lines.Skip(2).Select(l => VgaLine().Match(l)).Where(m => m.Success))
        {
            vga.TryAdd(match.Groups["id"].Value, match.Groups["vga"].Value);
        }

        var vms = (resources ?? [])
            .Where(r => r.Type == "qemu" && r.Template is not 1 && r.Vmid > 0)
            .Select(r =>
            {
                var id = r.Vmid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new VirtualMachine(id, r.Name is { Length: > 0 } name ? name : $"VM {id}", r.Status ?? "unknown", HasVncDisplay(vga.GetValueOrDefault(id)) ? ConsoleProtocols.Vnc : null, r.Node);
            })
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new HypervisorListing(vms, node);
    }

    /// <summary>A VM shows its display over VNC unless it has none or uses a serial terminal.</summary>
    /// <param name="vga">The configuration's <c>vga</c> value, e.g. <c>std</c>, <c>type=virtio,memory=32</c>, <c>serial0</c> or <c>none</c>.</param>
    internal static bool HasVncDisplay(string? vga)
    {
        var type = vga?.Split(',')[0].Trim() ?? "";
        type = type.StartsWith("type=", StringComparison.Ordinal) ? type["type=".Length..] : type;
        return !(type == "none" || type.StartsWith("serial", StringComparison.Ordinal));
    }

    /// <summary>A one-time VNC password. VNC uses only the first 8 characters of a password.</summary>
    public static string NewTicket() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 8);

    /// <summary>
    /// The command that relays <paramref name="vm"/>'s VNC display on stdin/stdout, after reading
    /// the ticket from the first line of input. A VM on another node of the cluster is reached the
    /// way Proxmox does: over SSH as root from this node, with the cluster's own host keys.
    /// </summary>
    public static async Task<string> VncProxyCommandAsync(SshClient client, string localNode, VirtualMachine vm, CancellationToken cancellationToken)
    {
        // The VM ID and node name come from pvesh and are checked here, so they are safe in the command line.
        if (!int.TryParse(vm.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) || vm.Node is not { } node || !NodeName().IsMatch(node))
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "Couldn't tell where this virtual machine runs.");
        }

        if (string.Equals(node, localNode, StringComparison.OrdinalIgnoreCase))
        {
            return $"{AsRoot}exec $p sh -c '{VncProxy}{vm.Id}'";
        }

        return await NodeSshCommandAsync(client, node, $"{VncProxy}{vm.Id}", "where this VM runs", cancellationToken);
    }

    /// <summary>
    /// The command that runs <paramref name="remote"/> (a fixed command of ours, in single quotes) on
    /// another node of the cluster, over SSH as root from this node with the cluster's own host keys,
    /// as Proxmox does.
    /// </summary>
    /// <param name="why">Why the node is needed, for errors, e.g. "where this VM runs".</param>
    public static async Task<string> NodeSshCommandAsync(SshClient client, string node, string remote, string why, CancellationToken cancellationToken)
    {
        if (!NodeName().IsMatch(node))
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"“{node}” isn't a Proxmox node name.");
        }

        var output = await KvmHost.RunAsync(client, AsRoot + "$p pvesh get /cluster/status --output-format json", cancellationToken, proxmox: true);
        List<ClusterMember>? members;
        try
        {
            members = JsonSerializer.Deserialize<List<ClusterMember>>(output.Trim(), Json);
        }
        catch (JsonException)
        {
            members = null;
        }

        var member = members?.FirstOrDefault(m => m.Type == "node" && string.Equals(m.Name, node, StringComparison.OrdinalIgnoreCase));
        if (member?.Ip is not { } ip || !IPAddress.TryParse(ip, out var address))
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't find the address of node “{node}”, {why}. Add that node to NKeeper and use it directly.");
        }

        if (member.Online is 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Node “{node}”, {why}, is offline.");
        }

        // Newer Proxmox versions keep each node's host keys in its own file; older ones in the cluster-wide known_hosts.
        return AsRoot
            + $"k=/etc/pve/nodes/{node}/ssh_known_hosts; o=''; [ -f \"$k\" ] && o=\"-o UserKnownHostsFile=$k -o GlobalKnownHostsFile=none\"; "
            + $"exec $p ssh -e none -T -o BatchMode=yes -o HostKeyAlias={node} $o -o ConnectTimeout=10 root@{address} '{remote}'";
    }

    /// <summary>Why Proxmox refused, with what NKeeper needs.</summary>
    public static string RefusedMessage(string error, int? exitStatus) =>
        $"Proxmox refused the request: {(error.Length > 0 ? error.Split('\n')[0] : $"exit code {exitStatus}")}. "
        + "NKeeper runs pvesh and qm on Proxmox nodes, which needs root: sign in as root, or as a user who may use sudo without a password.";

    private sealed record Resource(string? Type, int Vmid, string? Name, string? Node, string? Status, int? Template);

    private sealed record ClusterMember(string? Type, string? Name, string? Ip, int? Online);

    [GeneratedRegex(@"/qemu-server/(?<id>\d+)\.conf:vga:\s*(?<vga>\S*)")]
    private static partial Regex VgaLine();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})$")]
    private static partial Regex NodeName();
}
