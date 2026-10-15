using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// Verifies a username-or-email and password pair. Failures are deliberately indistinguishable
/// to callers (unknown account, wrong password, locked out) to avoid account enumeration.
/// </summary>
public sealed class CredentialValidator(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    ILogger<CredentialValidator> logger)
{
    private static readonly ApplicationUser TimingDummyUser = new();
    private static readonly Lazy<string> TimingDummyHash =
        new(() => new PasswordHasher<ApplicationUser>().HashPassword(TimingDummyUser, Guid.NewGuid().ToString()));

    public async Task<ApplicationUser?> ValidateAsync(string userNameOrEmail, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identifier = userNameOrEmail.Trim();
        var user = await userManager.FindByNameAsync(identifier);
        if (user is null && identifier.Contains('@', StringComparison.Ordinal))
        {
            user = await userManager.FindByEmailAsync(identifier);
        }

        if (user is null)
        {
            // Spend comparable time to a real verification so response timing does not reveal valid accounts.
            passwordHasher.VerifyHashedPassword(TimingDummyUser, TimingDummyHash.Value, password);
            logger.LogInformation("Sign-in failed: no matching account.");
            return null;
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return user;
        }

        var reason = result.IsLockedOut ? "locked out" : result.IsNotAllowed ? "not allowed" : "invalid password";
        logger.LogInformation("Sign-in failed for user {UserId}: {Reason}.", user.Id, reason);
        return null;
    }
}
