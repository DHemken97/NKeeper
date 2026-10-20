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

    /// <summary>
    /// An Active Directory account: it has no local password and signs in against the directory.
    /// Created at the user's first sign-in.
    /// </summary>
    public bool IsDirectoryAccount { get; set; }

    /// <summary>
    /// The identity provider's id for this person when the account signs in with single sign-on:
    /// it has no local password. Created at the user's first sign-in.
    /// </summary>
    public string? SingleSignOnSubject { get; set; }

    public bool IsSingleSignOnAccount => SingleSignOnSubject is not null;

    /// <summary>Last host-tree grouping levels selected by this user.</summary>
    public string[] WorkspaceGroupBy { get; set; } = [];

    /// <summary>Last host-tree sort selected by this user.</summary>
    public string WorkspaceSort { get; set; } = "name";
}
