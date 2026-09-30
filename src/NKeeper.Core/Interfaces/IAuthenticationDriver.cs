using System.Security.Claims;

namespace NKeeper.Core.Interfaces;

/// <summary>
/// Identity sources a user can authenticate against.
/// Persisted as strings, so renaming a member is a breaking change.
/// </summary>
public enum AuthDriverType
{
    LocalDatabase,
    ActiveDirectory,
    LinuxPam,
    OpenIdConnect
}

/// <summary>
/// Outcome of a primary (first-factor) authentication attempt.
/// </summary>
/// <param name="Succeeded">True when the credentials were accepted.</param>
/// <param name="Driver">The driver that produced this result.</param>
/// <param name="Username">Canonical username as reported by the identity source.</param>
/// <param name="ExternalId">Stable identifier in the source system (objectGUID, uid, sub, or the local user id).</param>
/// <param name="Claims">Claims harvested from the source (display name, email, group membership, ...).</param>
/// <param name="LocalUserId">Set by drivers backed by the local database; external drivers leave it null.</param>
/// <param name="FailureReason">Human-readable failure reason for logging. Never return it verbatim to clients.</param>
public sealed record AuthResult(
    bool Succeeded,
    AuthDriverType Driver,
    string? Username,
    string? ExternalId,
    IReadOnlyList<Claim> Claims,
    Guid? LocalUserId = null,
    string? FailureReason = null)
{
    public static AuthResult Success(
        AuthDriverType driver,
        string username,
        string externalId,
        IReadOnlyList<Claim> claims,
        Guid? localUserId = null) =>
        new(true, driver, username, externalId, claims, localUserId);

    public static AuthResult Failure(AuthDriverType driver, string reason) =>
        new(false, driver, null, null, [], null, reason);
}

/// <summary>
/// A pluggable username/password identity source.
/// Register implementations as scoped services; the login endpoint picks one by <see cref="DriverType"/>.
/// </summary>
public interface IAuthenticationDriver
{
    AuthDriverType DriverType { get; }

    /// <summary>Whether the driver is configured and usable on this host.</summary>
    bool IsEnabled { get; }

    Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct);
}
