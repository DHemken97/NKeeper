using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Ase;
using NKeeper.Api.Auditing;
using NKeeper.Api.Databases;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Controllers;

public sealed record AseCompareRequest
{
    /// <summary>The user's session on the other server.</summary>
    [Required]
    [StringLength(64)]
    public string OtherSessionId { get; init; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string Database { get; init; } = string.Empty;

    /// <summary>The database there; the same name when left out.</summary>
    [StringLength(255)]
    public string? OtherDatabase { get; init; }

    /// <summary>count(*) on every table, instead of the statistics' estimate.</summary>
    public bool Exact { get; init; }
}

/// <summary>
/// SAP ASE's Management tools beyond the explorer: Space and Replication Drift reports, comparing row counts with
/// another server, and copying a database to another server (dump, load, online; each step audited).
/// </summary>
[ApiController]
[Authorize]
[Route("api/ase/sessions/{id}")]
public sealed class AseToolsController(DatabaseSessionStore sessions, AuditLog audit, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("reports/{tool:regex(^(space|drift|license|dumps|errors|resources|security|maintenance)$)}")]
    public Task<ActionResult<ToolReport>> Report(string id, string tool, CancellationToken cancellationToken) =>
        DatabaseController.WithToolConnectionAsync(this, sessions, DatabaseEngine.Ase, id, connection =>
            AseDba.ReportAsync(connection, tool, timeProvider.GetUtcNow(), cancellationToken));

    [HttpPost("drift/compare")]
    public Task<ActionResult<ToolReport>> Compare(string id, AseCompareRequest request, CancellationToken cancellationToken) =>
        WithBothAsync(id, request.OtherSessionId, (here, hereSession, there, thereSession) =>
            AseDba.CompareAsync(
                here,
                hereSession.Server.ServerName,
                request.Database.Trim(),
                there,
                thereSession.Server.ServerName,
                string.IsNullOrWhiteSpace(request.OtherDatabase) ? request.Database.Trim() : request.OtherDatabase.Trim(),
                request.Exact,
                timeProvider.GetUtcNow(),
                cancellationToken));

    [HttpPost("copy/check")]
    public Task<ActionResult<AseCopyPlan>> CheckCopy(string id, AseCopyCheckRequest request, CancellationToken cancellationToken) =>
        WithBothAsync(id, request.TargetSessionId, (source, sourceSession, target, targetSession) =>
            AseCopy.CheckAsync(source, target, request, SameServer(sourceSession, targetSession), cancellationToken));

    /// <summary>Runs one step of a copy, for as long as it takes (it isn't stopped if the page goes away). Audited.</summary>
    [HttpPost("copy/run")]
    public async Task<ActionResult<ToolSection>> RunCopy(string id, AseCopyStepRequest request)
    {
        DatabaseSession? sourceSession = null;
        DatabaseSession? targetSession = null;
        var result = await WithBothAsync(id, request.TargetSessionId, async (source, s, target, t) =>
        {
            sourceSession = s;
            targetSession = t;
            var known = await DbQuery.ListAsync(source, AseCopy.KnownBackupServersSql, [], r => DbQuery.Text(r, 0)!, CancellationToken.None);
            return await AseCopy.RunAsync(source, target, request, known, CancellationToken.None);
        });

        if (result.Value is { } section && sourceSession is not null && targetSession is not null)
        {
            var outcome = section.Error is null ? "done" : $"failed: {section.Error}";
            await audit.RecordAsync(new AuditEntry(
                "ase.copy",
                request.Step == "dump" ? sourceSession.HostId : targetSession.HostId,
                $"{request.Database} · {sourceSession.Display} → {request.TargetDatabase} · {targetSession.Display} · {section.Command.ReplaceLineEndings(" ")} · {outcome}"));
        }

        return result;
    }

    private static bool SameServer(DatabaseSession a, DatabaseSession b) =>
        a.HostId == b.HostId && string.Equals(a.Server.ServerName, b.Server.ServerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A tool connection on each of two of the user's ASE sessions (this tab's and another server's).</summary>
    private async Task<ActionResult<T>> WithBothAsync<T>(string id, string otherId, Func<DbConnection, DatabaseSession, DbConnection, DatabaseSession, Task<T>> action)
    {
        try
        {
            var userId = User.GetUserId();
            var here = sessions.Get(id, userId, DatabaseEngine.Ase);
            var there = sessions.Get(otherId, userId, DatabaseEngine.Ase);
            await using var hereConnection = await here.OpenToolConnectionAsync(HttpContext.RequestAborted);
            await using var thereConnection = await there.OpenToolConnectionAsync(HttpContext.RequestAborted);
            return await action(hereConnection, here, thereConnection, there);
        }
        catch (DbException ex)
        {
            return this.SessionFailure(new SessionException(SessionErrorCodes.SqlError, ex.Message));
        }
        catch (SqlExplorerException ex)
        {
            return this.SessionFailure(new SessionException(SessionErrorCodes.BadRequest, ex.Message));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }
}
