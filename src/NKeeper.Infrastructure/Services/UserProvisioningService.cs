using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Data;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Infrastructure.Services;

/// <summary>
/// Maps a successful <see cref="AuthResult"/> to an NKeeper <see cref="User"/>, creating
/// and refreshing accounts for external identity sources (just-in-time provisioning).
/// </summary>
public sealed class UserProvisioningService(
    AppDbContext db,
    IOptions<AuthenticationOptions> options,
    ILogger<UserProvisioningService> logger)
{
    /// <summary>Returns the user with MFA credentials loaded, or null if no account exists and provisioning is off.</summary>
    public async Task<User?> ResolveUserAsync(AuthResult result, CancellationToken ct)
    {
        if (!result.Succeeded)
        {
            throw new ArgumentException("Cannot resolve a user from a failed authentication result.", nameof(result));
        }

        if (result.LocalUserId is { } localUserId)
        {
            return await db.Users
                .Include(u => u.MfaCredentials)
                .SingleOrDefaultAsync(u => u.Id == localUserId, ct);
        }

        var user = await db.Users
            .Include(u => u.MfaCredentials)
            .SingleOrDefaultAsync(u => u.AuthSource == result.Driver && u.ExternalId == result.ExternalId, ct);

        if (user is null)
        {
            if (!options.Value.AutoProvisionExternalUsers)
            {
                return null;
            }

            user = new User
            {
                Username = result.Username!,
                AuthSource = result.Driver,
                ExternalId = result.ExternalId
            };
            db.Users.Add(user);
            logger.LogInformation("Provisioned {Driver} user {Username}", result.Driver, result.Username);
        }

        // The identity source is authoritative for profile data and admin group membership.
        user.Username = result.Username!;
        user.DisplayName = FindClaim(result, ClaimTypes.GivenName) ?? user.DisplayName;
        user.Email = FindClaim(result, ClaimTypes.Email) ?? user.Email;
        user.IsAdmin = result.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == JwtTokenService.AdminRole);

        await db.SaveChangesAsync(ct);
        return user;
    }

    private static string? FindClaim(AuthResult result, string type) =>
        result.Claims.FirstOrDefault(c => c.Type == type)?.Value;
}
