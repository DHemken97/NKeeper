using System.ComponentModel.DataAnnotations;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>A machine or device that can be connected to. Holds no secrets itself.</summary>
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

    public Guid? GroupId { get; set; }

    public HostGroup? Group { get; set; }

    public List<HostService> Services { get; set; } = [];

    public List<Tag> Tags { get; set; } = [];

    /// <summary>Credentials usable with this host. Credentials are shared, not owned.</summary>
    public List<Credential> Credentials { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token, mapped to PostgreSQL's xmin system column.</summary>
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
