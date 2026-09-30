namespace NKeeper.Core.Entities;

public enum NodeOsFamily
{
    Unknown,
    Windows,
    Linux,
    MacOs,
    NetworkDevice,
    Other
}

/// <summary>A managed machine or device: server, VM, switch, appliance, ...</summary>
public class Node
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>DNS name or IP address used to reach the node.</summary>
    public required string Hostname { get; set; }

    public NodeOsFamily OsFamily { get; set; } = NodeOsFamily.Unknown;

    public string? Description { get; set; }

    public Guid? DatacenterId { get; set; }

    public Datacenter? Datacenter { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ConnectionProfile> ConnectionProfiles { get; set; } = new List<ConnectionProfile>();
}
