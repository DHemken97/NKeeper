using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>Well-known TCP ports: which NKeeper service they are (if any), and a name for the rest.</summary>
internal static class ScanPorts
{
    /// <summary>The ports an external scan probes (plus the host's own service ports).</summary>
    public static readonly int[] Common =
    [
        21, 22, 23, 25, 53, 80, 88, 110, 135, 139, 143, 389, 443, 445, 465, 587, 636, 993, 995,
        1433, 1521, 2049, 2179, 3306, 3389, 5432, 5900, 5901, 5985, 5986, 6379, 8006, 8080, 8443, 9090, 9100,
    ];

    private static readonly Dictionary<int, string> Names = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [88] = "Kerberos",
        [110] = "POP3", [135] = "Microsoft RPC", [139] = "NetBIOS", [143] = "IMAP", [389] = "LDAP", [443] = "HTTPS",
        [445] = "SMB (file sharing)", [465] = "SMTP (TLS)", [587] = "SMTP (submission)", [636] = "LDAPS", [993] = "IMAPS",
        [995] = "POP3S", [1433] = "SQL Server", [1521] = "Oracle", [2049] = "NFS", [2179] = "Hyper-V VM console",
        [3306] = "MySQL / MariaDB", [3389] = "Remote Desktop (RDP)", [5432] = "PostgreSQL", [5985] = "WinRM (HTTP)",
        [5986] = "WinRM (HTTPS)", [6379] = "Redis", [8006] = "Proxmox VE", [8080] = "HTTP (alternate)",
        [8443] = "HTTPS (alternate)", [9090] = "Cockpit", [9100] = "Printer (JetDirect)", [16509] = "libvirt",
    };

    /// <summary>The NKeeper service a port is, or null when NKeeper has none for it.</summary>
    public static ServiceProtocol? ProtocolFor(int port) => port switch
    {
        22 => ServiceProtocol.Ssh,
        3389 => ServiceProtocol.Rdp,
        >= 5900 and <= 5909 => ServiceProtocol.Vnc,
        23 => ServiceProtocol.Telnet,
        21 => ServiceProtocol.Ftp,
        80 or 8080 => ServiceProtocol.Http,
        443 or 8443 or 8006 or 9090 => ServiceProtocol.Https,
        1433 => ServiceProtocol.Ssms,
        5985 or 5986 => ServiceProtocol.WinRm,
        _ => null,
    };

    public static ScannedPort Describe(int port) =>
        new(port, Names.GetValueOrDefault(port) ?? (port is >= 5900 and <= 5909 ? $"VNC (display :{port - 5900})" : null), ProtocolFor(port));
}
