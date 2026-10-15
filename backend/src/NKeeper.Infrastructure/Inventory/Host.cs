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

    public List<HostService> Services { get; set; } = [];

    public List<HostTag> Tags { get; set; } = [];

    public List<HostCredential> Credentials { get; set; } = [];

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
    Other,
}
