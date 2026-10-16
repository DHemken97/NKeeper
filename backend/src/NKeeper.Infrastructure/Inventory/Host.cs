using System.ComponentModel.DataAnnotations;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>
/// A machine or device that can be connected to. Hosts are not placed in folders or groups;
/// the UI groups them by tag instead. Tags, services and credentials are owned by the host.
/// </summary>
public sealed class Host
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>DNS name. At least one of <see cref="Hostname"/> and <see cref="IpAddress"/> is set.</summary>
    public string? Hostname { get; set; }

    /// <summary>Normalized textual IPv4/IPv6 address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Normalized as AA:BB:CC:DD:EE:FF (e.g. for Wake-on-LAN later).</summary>
    public string? MacAddress { get; set; }

    public string? Description { get; set; }

    /// <summary>A virtual machine (guest) rather than physical hardware.</summary>
    public bool IsVirtualMachine { get; set; }

    /// <summary>
    /// The host this virtual machine runs on: a KVM/libvirt or Proxmox node. VMs can move between
    /// nodes (migration, HA), so scans of the nodes keep it current; null when unknown. Cleared when
    /// that host is deleted.
    /// </summary>
    public Guid? HypervisorId { get; set; }

    /// <summary>
    /// The VM's identity on its hypervisor platform, recorded by scans, e.g. <c>libvirt:&lt;uuid&gt;</c> or
    /// <c>proxmox:&lt;cluster&gt;/&lt;vmid&gt;</c>. It survives renames and moves between nodes.
    /// </summary>
    public string? VirtualMachineId { get; set; }

    /// <summary>When a scan last found this VM on <see cref="HypervisorId"/>; null when the link was set by hand.</summary>
    public DateTimeOffset? HypervisorSeenAtUtc { get; set; }

    public List<HostService> Services { get; set; } = [];

    public List<HostTag> Tags { get; set; } = [];

    public List<HostCredential> Credentials { get; set; } = [];

    /// <summary>SSH host keys and RDP certificates the users trusted (trust on first use). Cleared when the address changes.</summary>
    public List<HostTrustedKey> TrustedKeys { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's xmin system column. Every edit
    /// updates the host row (UpdatedAtUtc), so changes to owned rows also bump it.
    /// </summary>
    [Timestamp]
    public uint Version { get; set; }
}

/// <summary>A way of connecting to a host, e.g. SSH on port 22.</summary>
public sealed class HostService
{
    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    public ServiceProtocol Protocol { get; set; }

    /// <summary>Null means the protocol's default port.</summary>
    public int? Port { get; set; }

    /// <summary>Optional display label, e.g. "Management interface".</summary>
    public string? Label { get; set; }

    /// <summary>Preserves the order the user entered services in.</summary>
    public int Position { get; set; }

    /// <summary>RDP only: how the connection is secured. Null means <see cref="RdpSecurityMode.Auto"/>.</summary>
    public RdpSecurityMode? RdpSecurity { get; set; }

    /// <summary>
    /// Where the service listens when that isn't the host's own address: an iDRAC has its own
    /// management IP. Required for <see cref="ServiceProtocol.Idrac"/>; null for other services.
    /// </summary>
    public string? Address { get; set; }
}

/// <summary>
/// RDP transport security. Legacy "RDP security" (no TLS, no verifiable certificate) is
/// deliberately not offered.
/// </summary>
public enum RdpSecurityMode
{
    /// <summary>Network Level Authentication when the server supports it, otherwise TLS.</summary>
    Auto,

    /// <summary>Require Network Level Authentication (CredSSP), the Windows default.</summary>
    Nla,

    /// <summary>TLS only, for servers without NLA (e.g. xrdp). The server shows its own login screen.</summary>
    Tls,
}

/// <summary>
/// A Zabbix-style tag: a name with an optional value, e.g. <c>env: production</c>.
/// A host may carry the same name with different values (e.g. two <c>role</c> tags).
/// </summary>
public sealed class HostTag
{
    public const int NameMaxLength = 64;
    public const int ValueMaxLength = 255;

    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Empty string when the tag has no value.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Login details for one host. Secret material is stored only in encrypted form
/// (see <see cref="CredentialProtector"/>) and is never exposed through the API.
/// </summary>
public sealed class HostCredential
{
    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    /// <summary>Optional display label, e.g. "Local admin".</summary>
    public string? Label { get; set; }

    public string? Username { get; set; }

    public string? Domain { get; set; }

    /// <summary>Encrypted password (or private-key passphrase). Null when not set.</summary>
    public byte[]? ProtectedPassword { get; set; }

    /// <summary>Encrypted SSH private key. Null when not set.</summary>
    public byte[]? ProtectedPrivateKey { get; set; }

    public string? Notes { get; set; }

    /// <summary>Preserves the order the user entered credentials in.</summary>
    public int Position { get; set; }
}

/// <summary>
/// A server identity the users trusted for one protocol and port of a host: an SSH host key or
/// an RDP TLS certificate. A later connection presenting a different key for the same algorithm
/// is refused, which is how a man-in-the-middle is detected.
/// </summary>
public sealed class HostTrustedKey
{
    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    /// <summary>
    /// The protocol the identity was trusted for: <see cref="ServiceProtocol.Ssh"/>, <see cref="ServiceProtocol.Rdp"/>,
    /// <see cref="ServiceProtocol.Https"/>, or <see cref="ServiceProtocol.Idrac"/> for the iDRAC's own address
    /// (its HTTPS certificate and, for RACADM, its SSH host key).
    /// </summary>
    public ServiceProtocol Protocol { get; set; }

    public int Port { get; set; }

    /// <summary>SSH: the host key algorithm, e.g. "ssh-ed25519". RDP: "x509".</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 fingerprint. SSH: unpadded base64 as printed by OpenSSH (without "SHA256:").
    /// RDP: the certificate's SHA-256 as colon-separated upper-case hex.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset TrustedAtUtc { get; set; }

    public Guid? TrustedByUserId { get; set; }
}

public enum ServiceProtocol
{
    Ssh,
    Rdp,
    Vnc,
    Ssms,
    Telnet,
    Sftp,
    Ftp,
    Http,
    Https,
    WinRm,
    PowerShell,

    /// <summary>Windows RPC (DCE/RPC over TCP), usually through the endpoint mapper on port 135.</summary>
    Rpc,

    /// <summary>A KVM hypervisor managed with libvirt, reached over SSH: lists VMs and opens their SPICE consoles.</summary>
    Kvm,

    /// <summary>
    /// A Dell iDRAC at its own management address: Redfish (over HTTPS) for status and power, the
    /// HTML5 virtual console, and RACADM over SSH.
    /// </summary>
    Idrac,
    Other,
}
