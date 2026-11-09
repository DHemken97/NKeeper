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

    /// <summary>
    /// The ports a network (subnet) scan probes on each machine that answers: the common ones plus
    /// others that say what a device is (printers, NAS, hypervisors, containers, databases, apps).
    /// </summary>
    public static readonly int[] Network =
    [
        .. Common,
        111, 515, 548, 631, 873, 902, 1883, 2375, 2376, 3000, 5000, 5001, 5060, 5672, 6443, 7001,
        8000, 8008, 8081, 8888, 9000, 9200, 9443, 10000, 10050, 11211, 16509, 27017,
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
        [111] = "RPC portmapper", [515] = "Printer (LPD)", [548] = "AFP (Apple file sharing)", [631] = "Printer (IPP)",
        [873] = "rsync", [902] = "VMware ESXi", [1883] = "MQTT", [2375] = "Docker API", [2376] = "Docker API (TLS)",
        [3000] = "HTTP (app, e.g. Grafana)", [5000] = "HTTP (app, e.g. Synology)", [5001] = "HTTPS (app, e.g. Synology)",
        [5060] = "SIP (VoIP)", [5672] = "AMQP (RabbitMQ)", [6443] = "Kubernetes API", [7001] = "WebLogic",
        [8000] = "HTTP (alternate)", [8008] = "HTTP (alternate)", [8081] = "HTTP (alternate)", [8888] = "HTTP (alternate)",
        [9000] = "HTTP (app, e.g. Portainer)", [9200] = "Elasticsearch", [9443] = "HTTPS (app, e.g. Portainer)",
        [10000] = "Webmin", [10050] = "Zabbix agent", [11211] = "Memcached", [27017] = "MongoDB",
    };

    /// <summary>
    /// The NKeeper service a port can become, or null when NKeeper can't connect with it (FTP and
    /// WinRM ports, for example, are only listed for information).
    /// </summary>
    public static ServiceProtocol? ProtocolFor(int port) => port switch
    {
        22 => ServiceProtocol.Ssh,
        3389 => ServiceProtocol.Rdp,
        >= 5900 and <= 5909 => ServiceProtocol.Vnc,
        23 => ServiceProtocol.Telnet,
        80 or 8080 => ServiceProtocol.Http,
        443 or 8443 or 9090 => ServiceProtocol.Https,
        8006 => ServiceProtocol.Proxmox,
        1433 => ServiceProtocol.Ssms,
        1521 => ServiceProtocol.Oracle,
        _ => null,
    };

    public static ScannedPort Describe(int port) =>
        new(port, Names.GetValueOrDefault(port) ?? (port is >= 5900 and <= 5909 ? $"VNC (display :{port - 5900})" : null), ProtocolFor(port));
}
