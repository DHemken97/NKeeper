using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using NKeeper.Api.Auditing;
using NKeeper.Api.Databases;
using NKeeper.Api.Security;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Controllers;

/// <summary>The orphaned users after a fix, and what was done.</summary>
public sealed record SqlOrphanFixResponse(OrphanReport Report, string Message);

/// <summary>The orphaned users after several fixes, and what each did.</summary>
public sealed record SqlOrphanBulkResponse(OrphanReport Report, string Message, IReadOnlyList<SqlOrphanResult> Results);

/// <summary>
/// What only SQL Server has: Orphaned Users (database users whose login is missing or no longer has
/// their SID, e.g. after a restore). Sessions, queries, the explorer and blocking are in <see cref="DatabaseController"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mssql")]
public sealed class SqlServerController(DatabaseSessionStore sessions, AuditLog audit) : ControllerBase
{
    /// <summary>Database users whose login is missing or no longer has their SID (e.g. after a restore), and databases without an owner.</summary>
    [HttpGet("sessions/{id}/orphans")]
    [ProducesResponseType<OrphanReport>(StatusCodes.Status200OK)]
    public Task<ActionResult<OrphanReport>> Orphans(string id, CancellationToken cancellationToken) =>
        WithSqlAsync(id, connection => SqlOrphans.ReadAsync(connection, cancellationToken));

    /// <summary>Fixes one orphaned user (map, create its login, drop) or a missing owner, then lists what's left. Audited.</summary>
    [HttpPost("sessions/{id}/orphans/fix")]
    [ProducesResponseType<SqlOrphanFixResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlOrphanFixResponse>> FixOrphan(string id, SqlOrphanFix fix, CancellationToken cancellationToken) =>
        WithSqlAsync(id, async connection =>
        {
            var done = await SqlOrphans.FixAsync(connection, fix, cancellationToken);
            await AuditFixAsync(id, done);
            return new SqlOrphanFixResponse(await SqlOrphans.ReadAsync(connection, cancellationToken), done + ".");
        });

    /// <summary>Maps or drops several orphaned users; each that worked is audited, and each that didn't says why.</summary>
    [HttpPost("sessions/{id}/orphans/fix-many")]
    [ProducesResponseType<SqlOrphanBulkResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlOrphanBulkResponse>> FixManyOrphans(string id, SqlOrphanBulkFix request, CancellationToken cancellationToken) =>
        WithSqlAsync(id, async connection =>
        {
            var results = await SqlOrphans.FixManyAsync(connection, request.Fixes, cancellationToken);
            foreach (var done in results.Where(r => r.Succeeded))
            {
                await AuditFixAsync(id, done.Message);
            }

            var succeeded = results.Count(r => r.Succeeded);
            var failed = results.Count - succeeded;
            var verb = request.Fixes.All(f => f.Action == SqlOrphans.Drop) ? "Dropped" : request.Fixes.All(f => f.Action == SqlOrphans.Map) ? "Mapped" : "Fixed";
            var message = $"{verb} {succeeded} {(succeeded == 1 ? "user" : "users")}." + (failed == 0 ? "" : $" {failed} couldn't be.");
            return new SqlOrphanBulkResponse(await SqlOrphans.ReadAsync(connection, cancellationToken), message, results);
        });

    /// <summary>Maps every orphaned user that has a login of its own name to it (the usual repair after a restore). Audited.</summary>
    [HttpPost("sessions/{id}/orphans/fix-matched")]
    [ProducesResponseType<SqlOrphanFixResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlOrphanFixResponse>> FixMatchedOrphans(string id, CancellationToken cancellationToken) =>
        WithSqlAsync(id, async connection =>
        {
            var done = await SqlOrphans.FixAllMatchedAsync(connection, cancellationToken);
            foreach (var line in done)
            {
                await AuditFixAsync(id, line);
            }

            var message = done.Count == 0 ? "No orphaned user has a login of its own name." : $"Mapped {done.Count} {(done.Count == 1 ? "user" : "users")} to their logins.";
            return new SqlOrphanFixResponse(await SqlOrphans.ReadAsync(connection, cancellationToken), message);
        });

    private Task AuditFixAsync(string id, string detail)
    {
        var session = sessions.Get(id, User.GetUserId(), DatabaseEngine.SqlServer);
        return audit.RecordAsync(new AuditEntry("mssql.login_fix", session.HostId, $"{detail} · {session.Display}"));
    }

    private Task<ActionResult<T>> WithSqlAsync<T>(string id, Func<SqlConnection, Task<T>> action) =>
        DatabaseController.WithToolConnectionAsync(this, sessions, DatabaseEngine.SqlServer, id, (DbConnection connection) => action((SqlConnection)connection));
}
