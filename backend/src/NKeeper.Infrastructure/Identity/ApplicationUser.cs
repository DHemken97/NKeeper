using Microsoft.AspNetCore.Identity;

namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// An NKeeper user account. The <see cref="IdentityUser{TKey}.Id"/> GUID is the stable
/// identifier other features (workspaces, permissions, audit events) should reference.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
