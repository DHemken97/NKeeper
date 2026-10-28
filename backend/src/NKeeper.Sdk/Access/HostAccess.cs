using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NKeeper.Sdk.Security;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Sdk.Access;

/// <summary>Who is making a request, as far as host access is concerned.</summary>
public sealed record RequestUser(Guid Id, bool IsAdministrator)
{
    /// <summary>The request's server-side sign-in session, when known; it can hold the user's domain login.</summary>
    public string? SessionKey { get; init; }

    public static RequestUser From(ClaimsPrincipal principal) =>
        new(principal.GetUserId(), principal.IsInRole(SystemRoles.Administrator));
}

/// <summary>A user's permission on one host.</summary>
public enum HostPermission
{
    /// <summary>The host doesn't exist or isn't visible; callers respond 404 either way.</summary>
    None,

    Use,
    Manage,
}

/// <summary>
/// The single place that decides which hosts a user can see, connect to and manage.
/// Administrators can do everything; other users only what their grants allow.
/// </summary>
public sealed class HostAccess(NKeeperDbContext db)
{
    /// <summary>The hosts the user can see (and connect to).</summary>
    public IQueryable<Host> Visible(RequestUser user) =>
        user.IsAdministrator ? db.Hosts : db.Hosts.Where(Granted(user.Id, manageOnly: false));

    /// <summary>The hosts the user may edit and delete.</summary>
    public IQueryable<Host> Manageable(RequestUser user) =>
        user.IsAdministrator ? db.Hosts : db.Hosts.Where(Granted(user.Id, manageOnly: true));

    public async Task<HostPermission> GetPermissionAsync(Guid hostId, RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = db.Hosts.Where(h => h.Id == hostId);
        if (user.IsAdministrator)
        {
            return await hosts.AnyAsync(cancellationToken) ? HostPermission.Manage : HostPermission.None;
        }

        if (!await hosts.Where(Granted(user.Id, manageOnly: false)).AnyAsync(cancellationToken))
        {
            return HostPermission.None;
        }

        return await hosts.Where(Granted(user.Id, manageOnly: true)).AnyAsync(cancellationToken)
            ? HostPermission.Manage
            : HostPermission.Use;
    }

    /// <summary>The hosts the user may edit and delete; null means all of them (administrators).</summary>
    public async Task<HashSet<Guid>?> ManageableAsync(RequestUser user, CancellationToken cancellationToken) =>
        user.IsAdministrator
            ? null
            : [.. await db.Hosts.Where(Granted(user.Id, manageOnly: true)).Select(h => h.Id).ToListAsync(cancellationToken)];

    // Runs as SQL: ToUpper() becomes upper(), whereas string.Equals(..., StringComparison) can't be translated.
#pragma warning disable CA1862, CA1304, CA1311
    private Expression<Func<Host, bool>> Granted(Guid userId, bool manageOnly) =>
        h => db.HostAccessGrants.Any(g => g.UserId == userId
            && (!manageOnly || g.Level == HostAccessLevel.Manage)
            && (g.HostId == h.Id
                || (g.TagName != null && h.Tags.Any(t => t.Name.ToUpper() == g.TagName.ToUpper()
                    && (g.TagValue == null || t.Value.ToUpper() == g.TagValue.ToUpper())))));
#pragma warning restore CA1862, CA1304, CA1311
}

/// <summary>The user can see the host but not change it (responds 403).</summary>
public sealed class HostAccessDeniedException() : Exception("You can use this host but not change it. Ask an administrator for Manage access.");
