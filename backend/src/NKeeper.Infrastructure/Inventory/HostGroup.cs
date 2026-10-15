namespace NKeeper.Infrastructure.Inventory;

/// <summary>A named collection of hosts. Flat for now; nesting can be added with a parent reference.</summary>
public sealed class HostGroup
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Upper-cased name used to enforce case-insensitive uniqueness.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<Host> Hosts { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A free-form label. Tags exist only while at least one host uses them.</summary>
public sealed class Tag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public List<Host> Hosts { get; set; } = [];

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
