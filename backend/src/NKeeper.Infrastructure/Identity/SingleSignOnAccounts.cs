using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NKeeper.Infrastructure.Identity;

/// <summary>Who the identity provider says signed in, read from its token's claims.</summary>
/// <param name="Subject">The provider's lasting id for the person.</param>
public sealed record SingleSignOnIdentity(string Subject, string? UserName, string? Email, IReadOnlyCollection<string> Groups);

/// <summary>The NKeeper account to sign in, or why there is none.</summary>
public sealed record SingleSignOnResult(ApplicationUser? User, string? Refusal);

/// <summary>
/// Finds or creates the NKeeper account of someone the identity provider signed in. Accounts are
/// matched by the provider's subject only: a name or email that is already taken (by a local, an
/// Active Directory or another single sign-on account) is refused, never linked.
/// </summary>
public sealed class SingleSignOnAccounts(
    UserManager<ApplicationUser> userManager,
    IOptions<SingleSignOnOptions> options,
    TimeProvider timeProvider,
    ILogger<SingleSignOnAccounts> logger)
{
    public async Task<SingleSignOnResult> SignInAsync(SingleSignOnIdentity identity, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.RequiredGroups.Length > 0 && !InAny(identity.Groups, settings.RequiredGroups))
        {
            return Refuse(identity, "not in a required group");
        }

        var user = await userManager.Users.FirstOrDefaultAsync(u => u.SingleSignOnSubject == identity.Subject, cancellationToken);
        var isNew = user is null;
        if (user is null)
        {
            var userName = identity.UserName ?? identity.Email;
            if (userName is null)
            {
                return Refuse(identity, $"the token has no {settings.UserNameClaim} or {settings.EmailClaim} claim");
            }

            if (await userManager.FindByNameAsync(userName) is not null)
            {
                return Refuse(identity, "an NKeeper account already has that name");
            }

            user = new ApplicationUser
            {
                UserName = userName,
                SingleSignOnSubject = identity.Subject,
                CreatedAtUtc = timeProvider.GetUtcNow(),
            };
        }
        else if (user.IsDisabled)
        {
            return Refuse(identity, "the account is disabled");
        }

        // NKeeper requires a unique email; without the claim, a UPN-style username serves.
        var email = identity.Email ?? (identity.UserName?.Contains('@', StringComparison.Ordinal) == true ? identity.UserName : null) ?? user.Email;
        if (email is null)
        {
            return Refuse(identity, $"the token has no {settings.EmailClaim} claim");
        }

        user.Email = email;
        user.EmailConfirmed = true;
        var saved = isNew ? await userManager.CreateAsync(user) : await userManager.UpdateAsync(user);
        if (!saved.Succeeded)
        {
            return Refuse(identity, $"the NKeeper account couldn't be saved ({string.Join(" ", saved.Errors.Select(e => e.Description))})");
        }

        if (settings.AdministratorGroups.Length > 0)
        {
            var isAdministrator = InAny(identity.Groups, settings.AdministratorGroups);
            if (isAdministrator != await userManager.IsInRoleAsync(user, SystemRoles.Administrator))
            {
                _ = isAdministrator
                    ? await userManager.AddToRoleAsync(user, SystemRoles.Administrator)
                    : await userManager.RemoveFromRoleAsync(user, SystemRoles.Administrator);
            }
        }

        return new SingleSignOnResult(user, null);
    }

    private static bool InAny(IReadOnlyCollection<string> groups, string[] wanted) =>
        groups.Any(group => wanted.Contains(group, StringComparer.OrdinalIgnoreCase));

    private SingleSignOnResult Refuse(SingleSignOnIdentity identity, string reason)
    {
        logger.LogWarning("Single sign-on for {UserName} ({Subject}) refused: {Reason}.", identity.UserName ?? identity.Email, identity.Subject, reason);
        return new SingleSignOnResult(null, reason);
    }
}
