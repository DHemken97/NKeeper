using Microsoft.AspNetCore.Identity;

namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// An NKeeper user account. The <see cref="IdentityUser{TKey}.Id"/> GUID is the stable
/// identifier other features (workspaces, permissions, audit events) should reference.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Disabled accounts cannot sign in, and their sessions are revoked.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Set when an administrator assigns a password; the user must choose their own before doing anything else.</summary>
    public bool MustChangePassword { get; set; }
}
