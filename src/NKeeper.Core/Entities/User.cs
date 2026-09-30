using NKeeper.Core.Interfaces;

namespace NKeeper.Core.Entities;

/// <summary>
/// An NKeeper account. Local accounts carry a password hash; external accounts
/// (AD, PAM, OIDC) are provisioned on first login and linked by <see cref="ExternalId"/>.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Username { get; set; }

    public string? DisplayName { get; set; }

    public string? Email { get; set; }

    public AuthDriverType AuthSource { get; set; } = AuthDriverType.LocalDatabase;

    public string? ExternalId { get; set; }

    /// <summary>Only set for <see cref="AuthDriverType.LocalDatabase"/> accounts.</summary>
    public string? PasswordHash { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsAdmin { get; set; }

    public int AccessFailedCount { get; set; }

    public DateTime? LockoutEndUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<MfaCredential> MfaCredentials { get; set; } = new List<MfaCredential>();
}
