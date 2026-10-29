using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Contracts.Users;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Auditing;

/// <summary>What an audited action is about.</summary>
public enum AuditTarget
{
    None,

    /// <summary>The host in the route's <c>id</c>, or the one the action returns.</summary>
    Host,

    /// <summary>The user in the route's <c>id</c>, or the one the action returns (named in the detail).</summary>
    User,
}

/// <summary>
/// Records the action in the audit trail when it succeeds (a 2xx answer). The host or user is
/// looked up before the action runs, so deletions are recorded with the name they had.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditAttribute(string action, AuditTarget target = AuditTarget.Host) : ActionFilterAttribute
{
    public string Action { get; } = action;

    public AuditTarget Target { get; } = target;

    /// <summary>Fixed text for the detail, e.g. "external" for a scan.</summary>
    public string? Detail { get; init; }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var routeId = context.ActionArguments.TryGetValue("id", out var id) && id is Guid guid ? guid : (Guid?)null;
        var db = context.HttpContext.RequestServices.GetRequiredService<NKeeperDbContext>();
        string? before = null;
        if (routeId is { } key)
        {
            before = Target switch
            {
                AuditTarget.Host => await db.Hosts.Where(h => h.Id == key).Select(h => h.Name).FirstOrDefaultAsync(context.HttpContext.RequestAborted),
                AuditTarget.User => await db.Users.Where(u => u.Id == key).Select(u => u.UserName).FirstOrDefaultAsync(context.HttpContext.RequestAborted),
                _ => null,
            };
        }

        var executed = await next();
        var status = executed.Result switch
        {
            ObjectResult result => result.StatusCode ?? StatusCodes.Status200OK,
            IStatusCodeActionResult result => result.StatusCode ?? StatusCodes.Status200OK,
            _ => StatusCodes.Status200OK,
        };
        if ((executed.Exception is not null && !executed.ExceptionHandled) || status >= 400)
        {
            return;
        }

        var value = (executed.Result as ObjectResult)?.Value;
        if (value is HostImportResponse { DryRun: true })
        {
            return;
        }

        var host = value as HostResponse;
        var user = value as UserResponse;
        var detail = string.Join(" · ", new[]
        {
            Detail,
            Target == AuditTarget.User ? $"user {user?.UserName ?? before}" : null,
            value switch
            {
                IReadOnlyCollection<HostResponse> hosts => $"{hosts.Count} host{(hosts.Count == 1 ? "" : "s")}",
                HostImportResponse import => $"added {import.Added.Count}, updated {import.Updated.Count}, deleted {import.Deleted.Count}",
                Contracts.Zabbix.ZabbixImportResponse zabbix => $"added {zabbix.Created.Count}, skipped {zabbix.Skipped.Count}",
                _ => null,
            },
        }.Where(part => !string.IsNullOrEmpty(part)));

        var audit = context.HttpContext.RequestServices.GetRequiredService<AuditLog>();
        await audit.RecordAsync(new AuditEntry(
            Action,
            Target == AuditTarget.Host ? host?.Id ?? routeId : null,
            detail.Length > 0 ? detail : null,
            HostName: Target == AuditTarget.Host ? host?.Name ?? before : null));
    }
}
