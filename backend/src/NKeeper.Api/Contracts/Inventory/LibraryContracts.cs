using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Contracts.Inventory;

/// <summary>
/// Create/update a credential. Secrets are write-only: omit <see cref="Password"/> or
/// <see cref="PrivateKey"/> to keep the stored value, or set the matching Clear flag to remove it.
/// </summary>
public sealed record CredentialRequest
{
    [Required(ErrorMessage = "Enter a name.")]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(256)]
    public string? Domain { get; init; }

    [StringLength(1024)]
    public string? Password { get; init; }

    public bool ClearPassword { get; init; }

    [StringLength(16_384)]
    public string? PrivateKey { get; init; }

    public bool ClearPrivateKey { get; init; }

    [StringLength(2000)]
    public string? Notes { get; init; }

    /// <summary>The <c>version</c> from the last read. Required when updating.</summary>
    public uint? Version { get; init; }
}

/// <summary>A credential as seen by clients. Secret values are never included.</summary>
public sealed record CredentialResponse(
    Guid Id,
    string Name,
    string? Username,
    string? Domain,
    string? Notes,
    bool HasPassword,
    bool HasPrivateKey,
    int HostCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    uint Version);

public sealed record GroupRequest
{
    [Required(ErrorMessage = "Enter a name.")]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; init; }
}

public sealed record GroupResponse(Guid Id, string Name, string? Description, int HostCount);

public sealed record TagResponse(string Name, int HostCount);
