using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.EventLogs;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

/// <summary>Which Windows host, RPC service and login to read event logs with.</summary>
public record EventLogRequest : SessionStartMessage
{
    /// <summary>The domain of a login typed into the credentials prompt.</summary>
    [StringLength(256)]
    public string? Domain { get; init; }
}

/// <summary>Which events to read, newest first.</summary>
public sealed record EventLogEventsRequest : EventLogRequest
{
    [Required(ErrorMessage = "Choose a log.")]
    [StringLength(256)]
    public string Log { get; init; } = string.Empty;

    /// <summary>"Error", "Warning", "Information", "Audit Success" or "Audit Failure"; none for all.</summary>
    [MaxLength(5)]
    public IReadOnlyList<string>? Levels { get; init; }

    [StringLength(256)]
    public string? Source { get; init; }

    [Range(0, 65535, ErrorMessage = "An event ID is a number from 0 to 65535.")]
    public int? EventId { get; init; }

    /// <summary>Only events older than this record: the last one of the previous page.</summary>
    [Range(1, uint.MaxValue)]
    public long? BeforeRecord { get; init; }

    /// <summary>Only events at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }
}

public sealed record EventLogListResponse(IReadOnlyList<EventLogInfo> Logs);

/// <summary>
/// A Windows host's event logs, read only, over WMI with the host's RPC service and a login. The
/// server connects afresh for each request. Reading a log is audited; paging back through it isn't.
/// </summary>
[ApiController]
[Authorize]
[Route("api/eventlog")]
public sealed class EventLogController(AuditLog audit) : ControllerBase
{
    [HttpPost("logs")]
    [ProducesResponseType<EventLogListResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EventLogListResponse>> Logs(EventLogRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveAsync(request, cancellationToken);
            return new EventLogListResponse(await Reader.ListLogsAsync(target, cancellationToken));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    [HttpPost("events")]
    [ProducesResponseType<EventLogPage>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EventLogPage>> Events(EventLogEventsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var levels = request.Levels ?? [];
            if (levels.FirstOrDefault(level => !EventLogLevels.IsKnown(level)) is { } unknown)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, $"“{unknown}” isn't an event level.");
            }

            var target = await ResolveAsync(request, cancellationToken);
            var page = await Reader.ReadAsync(
                target, new EventLogQuery(request.Log, levels, request.Source, request.EventId, request.BeforeRecord, request.Since), cancellationToken);
            if (request.BeforeRecord is null)
            {
                await audit.RecordAsync(new AuditEntry("eventlog.read", request.HostId, $"{request.Log} log, as {target.Username}"));
            }

            return page;
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    private IEventLogReader Reader => HttpContext.RequestServices.GetRequiredService<IEventLogReader>();

    /// <summary>The host's RPC service and login; only the hosts and credentials the user can use, as for a session.</summary>
    private async Task<SessionTarget> ResolveAsync(EventLogRequest request, CancellationToken cancellationToken)
    {
        var target = await HttpContext.RequestServices.GetRequiredService<SessionTargetResolver>().ResolveAsync(
            request, RequestUser.From(User), ServiceProtocol.Rpc, Rpc.RpcClient.EndpointMapperPort, cancellationToken, request.Domain);
        SessionChecks.RefuseJumpHost(target, "The event log viewer");
        return target.Password is null
            ? throw new SessionException(SessionErrorCodes.MissingSecret, "Reading event logs needs a password.")
            : EventLogMapping.SplitDomain(target);
    }
}
