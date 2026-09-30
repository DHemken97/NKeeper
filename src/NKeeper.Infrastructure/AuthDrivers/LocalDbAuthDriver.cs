using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Data;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Infrastructure.AuthDrivers;

/// <summary>Authenticates accounts stored in the NKeeper database, with lockout after repeated failures.</summary>
public sealed class LocalDbAuthDriver(
    AppDbContext db,
    IPasswordHasher<User> passwordHasher,
    IOptions<AuthenticationOptions> options,
    TimeProvider timeProvider) : IAuthenticationDriver
{
    // Verified against when the user doesn't exist, so response time doesn't reveal valid usernames.
    private static readonly User DummyUser = new() { Username = "dummy" };
    private static readonly string DummyHash =
        new PasswordHasher<User>().HashPassword(DummyUser, Guid.NewGuid().ToString("N"));

    public AuthDriverType DriverType => AuthDriverType.LocalDatabase;

    public bool IsEnabled => options.Value.LocalDatabaseEnabled;

    public static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    public async Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return AuthResult.Failure(DriverType, "Local database authentication is disabled.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return AuthResult.Failure(DriverType, "Username and password are required.");
        }

        var normalized = NormalizeUsername(username);
        var user = await db.Users.SingleOrDefaultAsync(
            u => u.AuthSource == AuthDriverType.LocalDatabase && u.Username == normalized, ct);

        if (user?.PasswordHash is null)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return AuthResult.Failure(DriverType, "Unknown user.");
        }

        if (!user.IsEnabled)
        {
            return AuthResult.Failure(DriverType, "Account is disabled.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > now)
        {
            return AuthResult.Failure(DriverType, $"Account is locked out until {lockoutEnd:O}.");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= options.Value.MaxFailedAttempts)
            {
                user.LockoutEndUtc = now.AddMinutes(options.Value.LockoutMinutes);
                user.AccessFailedCount = 0;
            }

            await db.SaveChangesAsync(ct);
            return AuthResult.Failure(DriverType, "Invalid password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
        }

        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        await db.SaveChangesAsync(ct);

        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username) };
        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.GivenName, user.DisplayName));
        }

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        return AuthResult.Success(DriverType, user.Username, user.Id.ToString(), claims, user.Id);
    }
}
