namespace NKeeper.Core.Entities;

/// <summary>A site, region, cloud account or any other top-level grouping of nodes.</summary>
public class Datacenter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Location { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Node> Nodes { get; set; } = new List<Node>();
}
