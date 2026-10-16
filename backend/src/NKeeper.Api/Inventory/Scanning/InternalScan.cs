using System.Globalization;
using System.Net;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// An internal scan: one SSH connection to the host running one read-only shell script, which reads
/// the host's own state (hostname, OS, network interfaces, listening ports, local users). It sends
/// nothing to the network itself, so on the wire it is just an SSH login, like any admin's.
/// Linux and other Unix systems with a POSIX shell are supported.
/// </summary>
internal static class InternalScan
{
    private const string End = "end";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Each section starts with a "@@name" line. Every command is a local query: `ip route show`
    /// reads the routing table, `ss`/`netstat` list sockets, and nothing probes other machines.
    /// </summary>
    internal const string Script = """
        s() { printf '\n@@%s\n' "$1"; }
        s hostname; hostname -f 2>/dev/null || hostname 2>/dev/null || uname -n
        s os
        if [ -r /etc/os-release ]; then (. /etc/os-release; echo "${PRETTY_NAME:-$NAME $VERSION}")
        elif command -v sw_vers >/dev/null 2>&1; then echo "$(sw_vers -productName) $(sw_vers -productVersion)"; fi
        s kernel; uname -srm 2>/dev/null
        s connection; echo "$SSH_CONNECTION"
        s addresses; ip -o addr show 2>/dev/null
        s macs; for i in /sys/class/net/*; do [ -r "$i/address" ] && printf '%s %s\n' "${i##*/}" "$(cat "$i/address")"; done 2>/dev/null
        s route; ip route show default 2>/dev/null
        s listening; ss -Hltn 2>/dev/null || netstat -ltn 2>/dev/null
        s uidmin; sed -n 's/^UID_MIN[[:space:]]*//p' /etc/login.defs 2>/dev/null
        s users; getent passwd 2>/dev/null || cat /etc/passwd 2>/dev/null
        s libvirt; command -v virsh >/dev/null 2>&1 && echo yes
        s virt; systemd-detect-virt 2>/dev/null
        s libvirtvms
        if command -v virsh >/dev/null 2>&1; then
          v='virsh -r -c qemu:///system'; $v uri >/dev/null 2>&1 || v='virsh -c qemu:///system'
          for u in $($v list --all --uuid 2>/dev/null); do
            printf '%s\t%s\t%s\t%s\n' "$u" "$($v domname "$u" 2>/dev/null)" "$($v domstate "$u" 2>/dev/null | head -n 1)" \
              "$($v domiflist "$u" 2>/dev/null | awk 'NR > 2 && $5 ~ /:/ { printf "%s,", $5 }')"
          done
        fi
        s proxmox
        if [ -d /etc/pve/nodes ]; then
          printf 'cluster\t%s\n' "$(sed -n 's/^[[:space:]]*cluster_name:[[:space:]]*//p' /etc/pve/corosync.conf 2>/dev/null | head -n 1)"
          printf 'node\t%s\n' "$(uname -n | cut -d. -f1)"
          for f in /etc/pve/nodes/*/qemu-server/*.conf /etc/pve/nodes/*/lxc/*.conf; do
            [ -e "$f" ] || continue
            [ -r "$f" ] || { echo unreadable; continue; }
            n=${f#/etc/pve/nodes/}; n=${n%%/*}; id=${f##*/}; id=${id%.conf}
            case $f in */lxc/*) kind=lxc ;; *) kind=qemu ;; esac
            name=$(sed -n '/^\[/q; s/^name:[[:space:]]*//p; s/^hostname:[[:space:]]*//p' "$f" | head -n 1)
            macs=$(sed -n '/^\[/q; /^net[0-9]*:/p' "$f" | grep -oE '([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}' | tr '\n' ',')
            printf 'vm\t%s\t%s\t%s\t%s\t%s\n' "$kind" "$n" "$id" "$name" "$macs"
          done
        fi
        s end
        """;

    private static readonly string[] NoLoginShells = ["nologin", "false", "sync", "shutdown", "halt"];

    public static async Task<ScanFindings> RunAsync(SshClient client, CancellationToken cancellationToken)
    {
        // Run with sh whatever the login shell is (bash, zsh, fish...).
        using var command = client.CreateCommand($"sh -c '{Script.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        command.CommandTimeout = CommandTimeout;
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, "The host didn't finish the scan in time.");
        }

        return Parse(command.Result);
    }

    /// <summary>Turns the script's output into findings.</summary>
    /// <exception cref="SessionException">The host has no POSIX shell (e.g. Windows).</exception>
    internal static ScanFindings Parse(string output)
    {
        var sections = Sections(output);
        if (!sections.ContainsKey(End))
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                "The host didn't run the scan. Internal scans support Linux and other Unix systems (they need a POSIX shell, sh); Windows isn't supported yet.");
        }

        var notes = new List<string>();
        var hostname = First(sections, "hostname") is { } name && name != "localhost" && !name.Contains(' ', StringComparison.Ordinal) ? name : null;
        var os = First(sections, "os") ?? First(sections, "kernel");

        // SSH_CONNECTION is "client_ip client_port server_ip server_port": the address we reached.
        var connection = First(sections, "connection")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var serverIp = connection is { Length: 4 } ? NormalizeIp(connection[2]) : null;
        var sshPort = connection is { Length: 4 } && int.TryParse(connection[3], CultureInfo.InvariantCulture, out var p) ? p : (int?)null;

        var mac = FindMac(sections, serverIp, notes);
        var isKvmHost = First(sections, "libvirt") == "yes";

        var ports = new SortedSet<int>();
        foreach (var line in Lines(sections, "listening"))
        {
            // ss -Hltn and netstat -ltn both have the local address:port as the fourth column.
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 && SplitEndpoint(columns[3]) is var (address, port) && !IsLoopback(address))
            {
                ports.Add(port);
            }
        }

        if (isKvmHost && ports.RemoveWhere(port => port is >= 5900 and <= 5999) > 0)
        {
            notes.Add("Ports 5900 and up were left out: on a KVM host they're virtual machines' consoles. Open them from the KVM service instead.");
        }

        if (Lines(sections, "listening").Count == 0)
        {
            notes.Add("The listening ports couldn't be read (neither ss nor netstat is available).");
        }

        var uidMin = int.TryParse(First(sections, "uidmin"), CultureInfo.InvariantCulture, out var min) ? min : 1000;
        var users = new List<ScannedUser>();
        foreach (var line in Lines(sections, "users"))
        {
            var fields = line.Split(':');
            if (fields.Length < 7 || !int.TryParse(fields[2], CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }

            var shell = fields[6];
            var canLogIn = !NoLoginShells.Any(s => shell.EndsWith(s, StringComparison.Ordinal));
            if (canLogIn && fields[0].Length > 0 && (uid == 0 || (uid >= uidMin && uid != 65534)))
            {
                var fullName = fields[4].Split(',')[0].Trim();
                users.Add(new ScannedUser(fields[0], uid, fullName.Length > 0 ? fullName : null, shell.Length > 0 ? shell : "/bin/sh"));
            }
        }

        if (isKvmHost)
        {
            notes.Add($"libvirt (virsh) is installed, so this can be a KVM host, reached over SSH{(sshPort is { } port ? $" on port {port}" : "")}.");
        }

        var virtualization = First(sections, "virt") is { } virt && virt != "none" ? virt : null;
        var guests = ParseGuests(sections, notes);
        return new ScanFindings(hostname, serverIp, mac, os, [.. ports.Select(ScanPorts.Describe)], isKvmHost, users, notes, virtualization, guests);
    }

    /// <summary>libvirt domains ("uuid\tname\tstate\tmac,mac,") and Proxmox VMs/containers ("vm\tkind\tnode\tid\tname\tmac,").</summary>
    private static List<ScannedGuest> ParseGuests(Dictionary<string, List<string>> sections, List<string> notes)
    {
        var guests = new List<ScannedGuest>();
        foreach (var line in Lines(sections, "libvirtvms"))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 3 && Guid.TryParse(fields[0], out var uuid))
            {
                guests.Add(new ScannedGuest(
                    "libvirt", $"libvirt:{uuid:D}", fields[1].Trim(), fields[2].Trim(), null, true, Macs(fields.ElementAtOrDefault(3))));
            }
        }

        var proxmox = Lines(sections, "proxmox");
        string? Value(string key) => proxmox.Select(l => l.Split('\t', 2)).FirstOrDefault(f => f[0] == key && f.Length == 2)?[1].Trim() is { Length: > 0 } v ? v : null;
        var cluster = Value("cluster");
        var localNode = Value("node");
        // Lines are trimmed, so an empty name and MAC list leave only four fields.
        foreach (var fields in proxmox.Select(l => l.Split('\t')).Where(f => f[0] == "vm" && f.Length >= 4))
        {
            var (kind, node, id, name) = (fields[1], fields[2], fields[3], fields.ElementAtOrDefault(4)?.Trim() ?? "");
            guests.Add(new ScannedGuest(
                kind == "lxc" ? "proxmox-lxc" : "proxmox-qemu",
                $"proxmox:{cluster ?? node}/{id}",
                name.Length > 0 ? name : $"{(kind == "lxc" ? "CT" : "VM")} {id}",
                null,
                node,
                string.Equals(node, localNode, StringComparison.OrdinalIgnoreCase),
                Macs(fields.ElementAtOrDefault(5))));
        }

        if (proxmox.Contains("unreadable"))
        {
            notes.Add("Some Proxmox VM configurations couldn't be read: scan the node as root to see every VM and container in the cluster.");
        }

        return guests;
    }

    private static List<string> Macs(string? list) =>
        [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => NetworkAddress.TryNormalizeMac(m, out var mac) ? mac : null)
            .OfType<string>()
            .Distinct()];

    /// <summary>The MAC address of the interface the SSH connection arrived on (or of the default route's).</summary>
    private static string? FindMac(Dictionary<string, List<string>> sections, string? serverIp, List<string> notes)
    {
        var macs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(sections, "macs"))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && NetworkAddress.TryNormalizeMac(parts[1], out var mac) && mac != "00:00:00:00:00:00")
            {
                macs[parts[0]] = mac;
            }
        }

        // "2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0 ..."
        string? viaAddress = null;
        foreach (var line in Lines(sections, "addresses"))
        {
            var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 && serverIp is not null && NormalizeIp(columns[3].Split('/')[0]) == serverIp)
            {
                viaAddress = columns[1].TrimEnd(':').Split('@')[0];
                break;
            }
        }

        // "default via 10.0.0.1 dev eth0 proto dhcp ..."
        var route = First(sections, "route")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var devIndex = route is null ? -1 : Array.IndexOf(route, "dev");
        var viaRoute = devIndex >= 0 && devIndex + 1 < route!.Length ? route[devIndex + 1] : null;

        foreach (var candidate in new[] { viaAddress, viaRoute })
        {
            if (candidate is not null && macs.TryGetValue(candidate, out var mac))
            {
                return mac;
            }
        }

        var others = macs.Where(m => m.Key != "lo").ToList();
        if (others.Count == 1)
        {
            return others[0].Value;
        }

        notes.Add(others.Count == 0
            ? "No MAC address was found (the host doesn't expose /sys/class/net)."
            : "The host has several network interfaces and the one NKeeper reached couldn't be told apart, so no MAC address was picked.");
        return null;
    }

    private static Dictionary<string, List<string>> Sections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..].Trim()] = [];
            }
            else if (current is not null && line.Trim().Length > 0)
            {
                current.Add(line.Trim());
            }
        }

        return sections;
    }

    private static List<string> Lines(Dictionary<string, List<string>> sections, string name) =>
        sections.TryGetValue(name, out var lines) ? lines : [];

    private static string? First(Dictionary<string, List<string>> sections, string name) =>
        Lines(sections, name).FirstOrDefault() is { Length: > 0 } line ? line : null;

    /// <summary>"0.0.0.0:22", "[::]:22", "*:22", ":::22" → (address, port).</summary>
    private static (string Address, int Port)? SplitEndpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        return (endpoint[..colon].Trim('[', ']'), port);
    }

    private static bool IsLoopback(string address)
    {
        var host = address.Split('%')[0];
        return host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Plain text form, without an IPv6 zone and with IPv4-mapped addresses as IPv4.</summary>
    private static string? NormalizeIp(string value)
    {
        if (!IPAddress.TryParse(value.Split('%')[0], out var ip))
        {
            return null;
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
