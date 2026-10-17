using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// Verifies a username-or-email and password pair. Failures are deliberately indistinguishable
/// to callers (unknown account, wrong password, locked out) to avoid account enumeration.
/// </summary>
/// <remarks>
/// With Active Directory enabled, names NKeeper doesn't know (and its directory accounts) are
/// checked against the directory instead; a local account always keeps its local password.
/// </remarks>
public sealed class CredentialValidator(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IDirectoryAuthenticator directory,
    IOptions<ActiveDirectoryOptions> directoryOptions,
    TimeProvider timeProvider,
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

        var settings = directoryOptions.Value;
        if (settings.Enabled && (user?.IsDirectoryAccount ?? true))
        {
            // A directory account signs in with its account name however it was found (e.g. by email).
            var accountName = user?.UserName;
            if (accountName is not null || settings.TryGetAccountName(identifier, out accountName))
            {
                return await ValidateDirectoryAsync(user, accountName, password, cancellationToken);
            }
        }

        if (user is null || user.IsDirectoryAccount)
        {
            // Spend comparable time to a real verification so response timing does not reveal valid accounts.
            passwordHasher.VerifyHashedPassword(TimingDummyUser, TimingDummyHash.Value, password);
            logger.LogInformation("Sign-in failed: no matching account.");
            return null;
        }

        if (user.IsDisabled)
        {
            // Same generic failure as a wrong password; still verify to keep timing comparable.
            passwordHasher.VerifyHashedPassword(TimingDummyUser, TimingDummyHash.Value, password);
            logger.LogInformation("Sign-in failed for user {UserId}: account disabled.", user.Id);
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

    /// <param name="user">The account's NKeeper user; null at its first sign-in.</param>
    private async Task<ApplicationUser?> ValidateDirectoryAsync(
        ApplicationUser? user, string accountName, string password, CancellationToken cancellationToken)
    {
        if (user is not null)
        {
            if (user.IsDisabled)
            {
                logger.LogInformation("Sign-in failed for user {UserId}: account disabled.", user.Id);
                return null;
            }

            // NKeeper's own lockout also keeps password guessing from locking the domain account out.
            if (await userManager.IsLockedOutAsync(user))
            {
                logger.LogInformation("Sign-in failed for user {UserId}: locked out.", user.Id);
                return null;
            }
        }

        var account = await directory.AuthenticateAsync(accountName, password, cancellationToken);
        if (account is null)
        {
            if (user is not null)
            {
                await userManager.AccessFailedAsync(user);
            }

            return null;
        }

        if (user is null)
        {
            // The directory may spell the name differently from what was typed.
            user = await userManager.FindByNameAsync(account.UserName);
            if (user is { IsDirectoryAccount: false })
            {
                logger.LogWarning(
                    "Active Directory sign-in for {AccountName} refused: a local NKeeper account has that name.", account.UserName);
                return null;
            }
        }

        var isNew = user is null;
        user ??= new ApplicationUser
        {
            UserName = account.UserName,
            IsDirectoryAccount = true,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        // Usernames and emails must be unique in NKeeper; directory accounts without a mail
        // attribute get their implicit UPN.
        user.Email = account.Email ?? $"{account.UserName}@{directoryOptions.Value.DnsDomain}";
        user.EmailConfirmed = true;
        var saved = isNew
            ? await userManager.CreateAsync(user)
            : await userManager.UpdateAsync(user);
        if (!saved.Succeeded)
        {
            logger.LogError(
                "Active Directory sign-in for {AccountName} failed: the NKeeper account couldn't be saved ({Errors}).",
                account.UserName, string.Join(" ", saved.Errors.Select(e => e.Description)));
            return null;
        }

        if (account.IsAdministrator is { } isAdministrator && isAdministrator != await userManager.IsInRoleAsync(user, SystemRoles.Administrator))
        {
            _ = isAdministrator
                ? await userManager.AddToRoleAsync(user, SystemRoles.Administrator)
                : await userManager.RemoveFromRoleAsync(user, SystemRoles.Administrator);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        return user;
    }
}
