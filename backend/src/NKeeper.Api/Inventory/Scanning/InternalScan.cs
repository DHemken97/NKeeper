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
        s cpus; nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null
        s memory
        # Installed memory (lsmem) is closer to what the hardware has than the kernel's MemTotal.
        printf 'online %s\n' "$(lsmem -b --summary=only 2>/dev/null | sed -n 's/^Total online memory:[[:space:]]*//p')"
        sed -n 's/^MemTotal:[[:space:]]*\([0-9]*\).*/memtotal \1/p' /proc/meminfo 2>/dev/null
        printf 'bytes %s\n' "$(sysctl -n hw.memsize 2>/dev/null)"
        s serial
        # Only root may read it on most systems; sudo is tried only where it needs no password.
        f=/sys/class/dmi/id/product_serial
        if [ -r "$f" ]; then cat "$f"; elif [ -e "$f" ]; then sudo -n cat "$f" 2>/dev/null || echo '(unreadable)'; fi
        s libvirtvms
        if command -v virsh >/dev/null 2>&1; then
          v='virsh -r -c qemu:///system'; $v uri >/dev/null 2>&1 || v='virsh -c qemu:///system'
          for u in $($v list --all --uuid 2>/dev/null); do
            # Addresses come from the node's own DHCP leases and ARP table (local reads); vCPUs and
            # memory (KiB) are what the domain is given.
            printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$u" "$($v domname "$u" 2>/dev/null)" "$($v domstate "$u" 2>/dev/null | head -n 1)" \
              "$($v domiflist "$u" 2>/dev/null | awk 'NR > 2 && $5 ~ /:/ { printf "%s,", $5 }')" \
              "$( { $v domifaddr "$u" --source lease; $v domifaddr "$u" --source arp; } 2>/dev/null | awk '$3 ~ /^ipv/ { split($4, a, "/"); printf "%s,", a[1] }')" \
              "$($v dominfo "$u" 2>/dev/null | awk -F': *' '/^CPU\(s\)/ { c = $2 } /^Max memory/ { split($2, m, " ") } END { printf "%s\t%s", c, m[1] }')"
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
            # Static addresses: containers' ip=/ip6=, and VMs' cloud-init ipconfigN.
            ips=$(sed -n '/^\[/q; /^net[0-9]*:/p; /^ipconfig[0-9]*:/p' "$f" | grep -oE 'ip6?=[^,/ ]+' | cut -d= -f2 | grep -vE '^(dhcp|auto|manual)$' | tr '\n' ',')
            # Resources as configured: cores and sockets (VMs default to 1 x 1), memory in MiB.
            cores=$(sed -n '/^\[/q; s/^cores:[[:space:]]*//p' "$f" | head -n 1)
            sockets=$(sed -n '/^\[/q; s/^sockets:[[:space:]]*//p' "$f" | head -n 1)
            mem=$(sed -n '/^\[/q; s/^memory:[[:space:]]*\(current=\)\{0,1\}\([0-9]*\).*/\2/p' "$f" | head -n 1)
            printf 'vm\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$kind" "$n" "$id" "$name" "$macs" "$ips" "$cores" "$sockets" "$mem"
          done
        fi
        s neighbors
        # The host's existing neighbour (ARP/NDP) table: machines it has talked to, read locally.
        ip neigh show 2>/dev/null || arp -an 2>/dev/null
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
        var clientIp = connection is { Length: 4 } ? NormalizeIp(connection[0]) : null;
        var sshPort = connection is { Length: 4 } && int.TryParse(connection[3], CultureInfo.InvariantCulture, out var p) ? p : (int?)null;

        var mac = FindMac(sections, serverIp, notes);
        var hasLibvirt = First(sections, "libvirt") == "yes";
        var isProxmoxNode = Lines(sections, "proxmox").Any(l => l.StartsWith("node	", StringComparison.Ordinal));
        var isKvmHost = hasLibvirt || isProxmoxNode;

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
            var via = sshPort is { } port ? $" on port {port}" : "";
            notes.Add(isProxmoxNode
                ? $"This is a Proxmox VE node, so it can be a KVM host, reached over SSH{via} (as root, or a user with passwordless sudo)."
                : $"libvirt (virsh) is installed, so this can be a KVM host, reached over SSH{via}.");
        }

        var virtualization = First(sections, "virt") is { } virt && virt != "none" ? virt : null;
        var guests = ParseGuests(sections, notes);
        var neighbors = ParseNeighbors(sections, OwnAddresses(sections, serverIp, clientIp), notes);
        var cpus = PositiveInt(First(sections, "cpus"));
        // A VM's "serial number" is made up by its hypervisor: only hardware's is worth recording.
        var serial = virtualization is null ? Serial(First(sections, "serial"), notes) : null;
        return new ScanFindings(
            hostname, serverIp, mac, os, [.. ports.Select(ScanPorts.Describe)], isKvmHost, users, notes, virtualization, guests, neighbors, cpus, MemoryMiB(sections), serial);
    }

    /// <summary>Installed memory (lsmem, bytes), else the kernel's MemTotal (KiB), else macOS's hw.memsize (bytes), in MiB.</summary>
    private static int? MemoryMiB(Dictionary<string, List<string>> sections)
    {
        long? Value(string key) =>
            Lines(sections, "memory").Select(l => l.Split(' ', 2)).FirstOrDefault(f => f[0] == key && f.Length == 2) is { } found
            && long.TryParse(found[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

        var mib = Value("online") is { } online ? online / (1024 * 1024)
            : Value("memtotal") is { } kib ? kib / 1024
            : Value("bytes") is { } bytes ? bytes / (1024 * 1024)
            : (long?)null;
        return mib is > 0 and <= int.MaxValue ? (int)mib : null;
    }

    /// <summary>Placeholders firmware ships when no serial number was set.</summary>
    private static readonly string[] SerialPlaceholders =
        ["to be filled by o.e.m.", "default string", "system serial number", "not specified", "not applicable", "none", "n/a", "0123456789", "chassis serial number"];

    /// <summary>A real serial number, or null for empty values and firmware placeholders.</summary>
    internal static string? Serial(string? value, List<string>? notes = null)
    {
        if (value == "(unreadable)")
        {
            notes?.Add("The serial number can only be read as root: scan as root, or as a user with passwordless sudo, to record it.");
            return null;
        }

        var serial = value?.Trim();
        return string.IsNullOrEmpty(serial) || serial.Length > 128 || serial.All(c => c is '0' or ' ' or '.' or '-')
            || SerialPlaceholders.Contains(serial, StringComparer.OrdinalIgnoreCase)
            ? null
            : serial;
    }

    private static int? PositiveInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>libvirt domains ("uuid\tname\tstate\tmac,\tip,\tvcpus\tKiB") and Proxmox VMs/containers ("vm\tkind\tnode\tid\tname\tmac,\tip,\tcores\tsockets\tMiB").</summary>
    private static List<ScannedGuest> ParseGuests(Dictionary<string, List<string>> sections, List<string> notes)
    {
        var guests = new List<ScannedGuest>();
        foreach (var line in Lines(sections, "libvirtvms"))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 3 && Guid.TryParse(fields[0], out var uuid))
            {
                // virsh reports memory in KiB.
                var memoryKib = PositiveInt(fields.ElementAtOrDefault(6));
                guests.Add(new ScannedGuest(
                    "libvirt", $"libvirt:{uuid:D}", fields[1].Trim(), fields[2].Trim(), null, true, Macs(fields.ElementAtOrDefault(3)), Ips(fields.ElementAtOrDefault(4)))
                {
                    CpuCores = PositiveInt(fields.ElementAtOrDefault(5)),
                    MemoryMiB = memoryKib is >= 1024 ? memoryKib / 1024 : null,
                });
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
                Macs(fields.ElementAtOrDefault(5)),
                Ips(fields.ElementAtOrDefault(6)))
            {
                // VMs: cores x sockets, each 1 when not set. Containers without "cores" may use every core of the node.
                CpuCores = kind == "lxc"
                    ? PositiveInt(fields.ElementAtOrDefault(7))
                    : (PositiveInt(fields.ElementAtOrDefault(7)) ?? 1) * (PositiveInt(fields.ElementAtOrDefault(8)) ?? 1),
                MemoryMiB = PositiveInt(fields.ElementAtOrDefault(9)),
            });
        }

        if (proxmox.Contains("unreadable"))
        {
            notes.Add("Some Proxmox VM configurations couldn't be read: scan the node as root to see every VM and container in the cluster.");
        }

        return guests;
    }

    /// <summary>The most neighbour entries recorded, so a host with a huge table can't flood the findings.</summary>
    private const int MaxNeighbors = 512;

    private static readonly char[] Whitespace = [' ', '\t'];

    /// <summary>
    /// The host's own addresses, which shouldn't be reported as discovered neighbours: every address
    /// on its interfaces, plus the address NKeeper reached it on and NKeeper's own (both ends of the
    /// SSH connection, which the host's neighbour table always holds).
    /// </summary>
    private static HashSet<string> OwnAddresses(Dictionary<string, List<string>> sections, string? serverIp, string? clientIp)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in new[] { serverIp, clientIp })
        {
            if (address is not null)
            {
                own.Add(address);
            }
        }

        // "2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0 ..." (inet6 for IPv6).
        foreach (var line in Lines(sections, "addresses"))
        {
            var columns = line.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
            var index = Array.FindIndex(columns, c => c is "inet" or "inet6");
            if (index >= 0 && index + 1 < columns.Length && NormalizeIp(columns[index + 1].Split('/')[0]) is { } ip)
            {
                own.Add(ip);
            }
        }

        return own;
    }

    /// <summary>
    /// The neighbour (ARP/NDP) table: machines the host has talked to recently. Only complete entries
    /// with a real MAC are kept, deduplicated by address and without the host's own addresses; these
    /// become candidate hosts to match or add. Nothing was probed — the table was already there.
    /// </summary>
    private static List<ScannedNeighbor> ParseNeighbors(Dictionary<string, List<string>> sections, HashSet<string> own, List<string> notes)
    {
        var neighbors = new List<ScannedNeighbor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(sections, "neighbors"))
        {
            var (ip, mac) = ParseNeighborLine(line);
            if (ip is null || mac is null || own.Contains(ip) || !seen.Add(ip))
            {
                continue;
            }

            if (neighbors.Count >= MaxNeighbors)
            {
                notes.Add($"The neighbour table had more than {MaxNeighbors} entries; only the first {MaxNeighbors} were read.");
                break;
            }

            neighbors.Add(new ScannedNeighbor(ip, mac));
        }

        return neighbors;
    }

    /// <summary>
    /// One neighbour line, from `ip neigh show` ("IP dev IF lladdr MAC STATE") or `arp -an`
    /// ("? (IP) at MAC [ether] on IF"), as (IP, MAC). Null when it has no usable unicast entry
    /// (an incomplete or failed lookup, loopback, link-local, or a multicast/broadcast address).
    /// </summary>
    internal static (string? Ip, string? Mac) ParseNeighborLine(string line)
    {
        var tokens = line.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        string? rawMac = null;
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] is "lladdr" or "at")
            {
                rawMac = tokens[i + 1];
            }
        }

        // arp -an wraps the IP in parentheses; ip neigh puts it first.
        var parenthesized = Array.Find(tokens, t => t.StartsWith('(') && t.EndsWith(')'));
        var rawIp = parenthesized?.Trim('(', ')') ?? (tokens.Length > 0 ? tokens[0] : null);
        if (rawIp is null || rawMac is null
            || NormalizeIp(rawIp) is not { } ip
            || !NetworkAddress.TryNormalizeMac(rawMac, out var mac) || mac == "00:00:00:00:00:00"
            || !IPAddress.TryParse(ip, out var address)
            || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal
            || ip.StartsWith("169.254.", StringComparison.Ordinal) || IsNonUnicast(address))
        {
            return (null, null);
        }

        return (ip, mac);
    }

    /// <summary>Multicast or broadcast addresses, which aren't a single machine.</summary>
    private static bool IsNonUnicast(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? address.IsIPv6Multicast
            : address.GetAddressBytes()[0] >= 224; // 224.0.0.0 and up: multicast, reserved, and 255.255.255.255.

    /// <summary>Addresses a host could be reached on: IPv4 first, without loopback or link-local ones.</summary>
    private static List<string> Ips(string? list) =>
        [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ip => NetworkAddress.TryNormalizeIp(ip, out var normalized) ? IPAddress.Parse(normalized) : null)
            .OfType<IPAddress>()
            .Where(ip => !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .OrderBy(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .Select(ip => ip.ToString())
            .Distinct()];

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
