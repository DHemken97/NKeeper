using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Users;
using NKeeper.Api.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Users;

/// <summary>
/// Administrator operations on accounts. Guards against locking everyone out (an administrator
/// can't demote, disable or delete themselves, and at least one active administrator remains),
/// and ends a user's sessions whenever their access is cut or their password is reset.
/// </summary>
public sealed class UserAdministration(
    NKeeperDbContext db,
    UserManager<ApplicationUser> userManager,
    SessionRevoker sessions,
    SessionLimiter remoteSessions,
    Web.WebSessionStore webSessions,
    Kvm.ConsoleSessionStore consoles,
    TimeProvider timeProvider,
    ILogger<UserAdministration> logger)
{
    public async Task<List<UserSummaryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var admins = await AdministratorIdsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.NormalizedUserName)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.Email,
                u.IsDisabled,
                u.MustChangePassword,
                u.LockoutEnd,
                u.CreatedAtUtc,
                Grants = db.HostAccessGrants.Count(g => g.UserId == u.Id),
            })
            .ToListAsync(cancellationToken);
        return [.. users.Select(u => new UserSummaryResponse(
            u.Id, u.UserName!, u.Email, RoleOf(u.Id, admins), u.IsDisabled, u.MustChangePassword, u.LockoutEnd > now, u.Grants, u.CreatedAtUtc))];
    }

    public async Task<UserResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var grants = await db.HostAccessGrants.AsNoTracking()
            .Where(g => g.UserId == id)
            .OrderBy(g => g.HostId == null).ThenBy(g => g.TagName).ThenBy(g => g.TagValue)
            .Select(g => new HostAccessGrantResponse(
                g.Id, g.HostId, db.Hosts.Where(h => h.Id == g.HostId).Select(h => h.Name).FirstOrDefault(), g.TagName, g.TagValue, g.Level))
            .ToListAsync(cancellationToken);
        var admins = await AdministratorIdsAsync(cancellationToken);
        return new UserResponse(
            user.Id, user.UserName!, user.Email, RoleOf(user.Id, admins), user.IsDisabled, user.MustChangePassword,
            user.LockoutEnd > timeProvider.GetUtcNow(), user.CreatedAtUtc, grants);
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            EmailConfirmed = true,
            MustChangePassword = request.MustChangePassword,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        ThrowOnFailure(await userManager.CreateAsync(user, request.Password), defaultField: "password");
        if (request.Role == UserRole.Administrator)
        {
            ThrowOnFailure(await userManager.AddToRoleAsync(user, SystemRoles.Administrator), defaultField: "role");
        }

        logger.LogInformation("User {ActorId} created user {UserId} ({UserName}) as {Role}", actorId, user.Id, user.UserName, request.Role);
        return (await GetAsync(user.Id, cancellationToken))!;
    }

    public async Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return null;
        }

        var wasAdmin = await userManager.IsInRoleAsync(user, SystemRoles.Administrator);
        var makeAdmin = request.Role == UserRole.Administrator;
        if (id == actorId && (!makeAdmin || request.IsDisabled))
        {
            throw new InventoryValidationException(request.IsDisabled ? "isDisabled" : "role", "You can't remove your own administrator access or disable yourself.");
        }

        if (wasAdmin && !user.IsDisabled && (!makeAdmin || request.IsDisabled))
        {
            await EnsureAnotherActiveAdministratorAsync(id, request.IsDisabled ? "isDisabled" : "role", cancellationToken);
        }

        var accessCut = (wasAdmin && !makeAdmin) || (request.IsDisabled && !user.IsDisabled);
        user.Email = request.Email.Trim();
        user.IsDisabled = request.IsDisabled;
        ThrowOnFailure(await userManager.UpdateAsync(user), defaultField: "email");
        if (makeAdmin && !wasAdmin)
        {
            ThrowOnFailure(await userManager.AddToRoleAsync(user, SystemRoles.Administrator), defaultField: "role");
        }
        else if (!makeAdmin && wasAdmin)
        {
            ThrowOnFailure(await userManager.RemoveFromRoleAsync(user, SystemRoles.Administrator), defaultField: "role");
        }

        if (accessCut || makeAdmin != wasAdmin)
        {
            // Roles live in the session cookie, so a role change takes effect through a fresh sign-in.
            await EndSessionsAsync(id, cancellationToken);
        }

        logger.LogInformation(
            "User {ActorId} updated user {UserId}: role {Role}, disabled {Disabled}", actorId, id, request.Role, request.IsDisabled);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> ResetPasswordAsync(Guid id, ResetPasswordRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return false;
        }

        // An administrator reset needs no emailed token: check the policy, then set the hash directly.
        foreach (var validator in userManager.PasswordValidators)
        {
            ThrowOnFailure(await validator.ValidateAsync(userManager, user, request.Password), defaultField: "password");
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, request.Password);
        await userManager.UpdateSecurityStampAsync(user);
        user.MustChangePassword = request.MustChangePassword && id != actorId;
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        ThrowOnFailure(await userManager.UpdateAsync(user), defaultField: "password");
        await EndSessionsAsync(id, cancellationToken);
        logger.LogInformation("User {ActorId} reset the password of user {UserId}", actorId, id);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return false;
        }

        if (id == actorId)
        {
            throw new InventoryValidationException("id", "You can't delete your own account.");
        }

        if (!user.IsDisabled && await userManager.IsInRoleAsync(user, SystemRoles.Administrator))
        {
            await EnsureAnotherActiveAdministratorAsync(id, "id", cancellationToken);
        }

        await EndSessionsAsync(id, cancellationToken);
        ThrowOnFailure(await userManager.DeleteAsync(user), defaultField: "id");
        logger.LogInformation("User {ActorId} deleted user {UserId} ({UserName})", actorId, id, user.UserName);
        return true;
    }

    /// <summary>Replaces the user's host access grants. Takes effect on the user's next request.</summary>
    public async Task<UserResponse?> ReplaceGrantsAsync(Guid id, ReplaceGrantsRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, cancellationToken))
        {
            return null;
        }

        var hostIds = request.Grants.Where(g => g.HostId is not null).Select(g => g.HostId!.Value).Distinct().ToList();
        var existingHosts = await db.Hosts.Where(h => hostIds.Contains(h.Id)).Select(h => h.Id).ToListAsync(cancellationToken);
        var missing = hostIds.Except(existingHosts).ToList();
        if (missing.Count > 0)
        {
            throw new InventoryValidationException("grants", "One or more hosts no longer exist. Reload and try again.");
        }

        var now = timeProvider.GetUtcNow();
        var grants = request.Grants
            .Select(g => new HostAccessGrant
            {
                UserId = id,
                HostId = g.HostId,
                TagName = g.HostId is null ? g.TagName!.Trim() : null,
                TagValue = g.HostId is null ? InventoryText.TrimToNull(g.TagValue) : null,
                Level = g.Level!.Value,
                CreatedAtUtc = now,
                CreatedByUserId = actorId,
            })
            // One grant per target; the higher level wins.
            .GroupBy(g => (g.HostId, g.TagName?.ToUpperInvariant(), g.TagValue?.ToUpperInvariant()))
            .Select(group => group.OrderByDescending(g => g.Level).First())
            .ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.HostAccessGrants.Where(g => g.UserId == id).ExecuteDeleteAsync(cancellationToken);
        db.HostAccessGrants.AddRange(grants);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {ActorId} set {Count} host access grants for user {UserId}", actorId, grants.Count, id);
        return await GetAsync(id, cancellationToken);
    }

    private async Task EndSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await sessions.RevokeAllAsync(userId, cancellationToken);
        remoteSessions.TerminateAll(userId);
        webSessions.EndAll(userId);
        consoles.EndAll(userId);
    }

    private async Task EnsureAnotherActiveAdministratorAsync(Guid exceptUserId, string field, CancellationToken cancellationToken)
    {
        var admins = await AdministratorIdsAsync(cancellationToken);
        var others = await db.Users.CountAsync(u => admins.Contains(u.Id) && u.Id != exceptUserId && !u.IsDisabled, cancellationToken);
        if (others == 0)
        {
            throw new InventoryValidationException(field, "NKeeper needs at least one active administrator. Make someone else an administrator first.");
        }
    }

    private async Task<HashSet<Guid>> AdministratorIdsAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.GetUsersInRoleAsync(SystemRoles.Administrator);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. users.Select(u => u.Id)];
    }

    private static UserRole RoleOf(Guid id, HashSet<Guid> admins) => admins.Contains(id) ? UserRole.Administrator : UserRole.User;

    /// <summary>Turns Identity errors into field errors (password policy, duplicate names, …).</summary>
    internal static void ThrowOnFailure(IdentityResult result, string defaultField)
    {
        if (result.Succeeded)
        {
            return;
        }

        var field = result.Errors.First().Code switch
        {
            "DuplicateUserName" or "InvalidUserName" => "userName",
            "DuplicateEmail" or "InvalidEmail" => "email",
            "PasswordMismatch" => "currentPassword",
            _ => defaultField,
        };
        var message = string.Join(" ", result.Errors.Select(e => e.Description));
        throw new InventoryValidationException(field, message);
    }
}
