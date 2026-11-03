using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.SqlServer;
using NKeeper.Api.Web;

namespace NKeeper.Api.Controllers;

/// <summary>Opens a SQL Server session: the host's SQL Server service (an instance) and a SQL login.</summary>
public sealed record SqlServerOpenRequest : SessionStartMessage
{
    /// <summary>The server's certificate fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>The database to start in; the login's default database when empty.</summary>
    [StringLength(128)]
    public string? Database { get; init; }
}

/// <summary>Either the open session (with what the server says about itself), or its certificate to trust first.</summary>
/// <param name="Renewed">The certificate replaces a trusted one: SQL Server made itself a new self-signed certificate (it does at every start).</param>
public sealed record SqlServerOpenResponse(
    string Status, string? SessionId, string Target, SqlServerInfo? Server, string? Database, WebCertificateResponse? Certificate, bool Renewed = false);

public sealed record SqlQueryRequest
{
    [Required(ErrorMessage = "Enter a query.")]
    [StringLength(1_000_000, ErrorMessage = "Queries can be at most a million characters.")]
    public string Sql { get; init; } = string.Empty;

    /// <summary>Lines above the query in the editor (when running a selection), so errors point at the right line.</summary>
    [Range(0, 1_000_000)]
    public int LineOffset { get; init; }
}

public sealed record SqlDatabaseRequest
{
    [Required]
    [StringLength(128)]
    public string Name { get; init; } = string.Empty;
}

public sealed record SqlExploreRequest
{
    [Required]
    public SqlObjectNode Node { get; init; } = new();
}

public sealed record SqlScriptRequest
{
    [Required]
    public SqlObjectNode Node { get; init; } = new();

    [Required]
    [AllowedValues(SqlScripter.SelectTop, SqlScripter.Create, SqlScripter.Execute, ErrorMessage = "Choose a script.")]
    public string Action { get; init; } = string.Empty;
}

public sealed record SqlKillRequest
{
    [Range(1, 32767, ErrorMessage = "Choose a session.")]
    public int SessionId { get; init; }
}

public sealed record SqlScriptResponse(string Sql);

/// <summary>The orphaned users after a fix, and what was done.</summary>
public sealed record SqlOrphanFixResponse(OrphanReport Report, string Message);

/// <summary>
/// SQL Server, like a small SSMS: query windows on a session of their own (results as grids or text,
/// with messages), an object explorer, scripts of objects, and the blocking manager (blocking chains
/// under their lead blockers, and KILL). Each session lives on the server until its tab closes; only
/// the user who opened it can use it.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mssql")]
public sealed class SqlServerController(SqlServerSessionStore sessions, AuditLog audit, TimeProvider timeProvider) : ControllerBase
{
    [HttpPost("sessions")]
    [ProducesResponseType<SqlServerOpenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlServerOpenResponse>> Open(SqlServerOpenRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var connector = HttpContext.RequestServices.GetRequiredService<SqlServerConnector>();
            var result = await connector.OpenAsync(request, RequestUser.From(User), cancellationToken);
            if (result.Prompt is { } certificate)
            {
                return new SqlServerOpenResponse(
                    "trustRequired",
                    null,
                    result.Target,
                    null,
                    null,
                    new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter),
                    result.Renewed);
            }

            var session = result.Session!;
            if (!sessions.TryAdd(session))
            {
                await session.DisposeAsync();
                throw new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {SqlServerSessionStore.MaxSessionsPerUser} SQL Server sessions open. Close one and try again.");
            }

            return new SqlServerOpenResponse("ready", session.Id, result.Target, session.Server, session.Connection.Database, null);
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    [HttpDelete("sessions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Close(string id)
    {
        try
        {
            sessions.Get(id, User.GetUserId());
            await sessions.EndAsync(id, "closed by the user");
        }
        catch (SessionException)
        {
            // Already gone: closing is done either way.
        }

        return NoContent();
    }

    /// <summary>An open tab says it's still there, so its session isn't closed as idle.</summary>
    [HttpPost("sessions/{id}/keepalive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult KeepAlive(string id) => Run(() =>
    {
        sessions.Get(id, User.GetUserId());
        return NoContent();
    });

    [HttpPost("sessions/{id}/query")]
    [ProducesResponseType<SqlQueryResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlQueryResult>> Query(string id, SqlQueryRequest request)
    {
        try
        {
            var session = sessions.Get(id, User.GetUserId());
            using var lease = session.TryBeginQuery(HttpContext.RequestAborted)
                ?? throw new SessionException(SessionErrorCodes.Busy, "A query is already running in this session. Wait for it, or cancel it.");
            var result = await SqlQueryRunner.RunAsync(session.Connection, request.Sql, request.LineOffset, lease.Token);
            session.LastActivityUtc = timeProvider.GetUtcNow();
            if (result.ConnectionLost)
            {
                await sessions.EndAsync(id, "connection lost");
            }

            return result;
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    [HttpPost("sessions/{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Cancel(string id) => Run(() =>
    {
        sessions.Get(id, User.GetUserId()).Cancel();
        return NoContent();
    });

    /// <summary>The databases to choose from, for the toolbar's database list.</summary>
    [HttpGet("sessions/{id}/databases")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public Task<ActionResult<IReadOnlyList<string>>> Databases(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync<IReadOnlyList<string>>(id, async connection => [.. await SqlObjectExplorer.QueryAsync(
            connection,
            "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name",
            [],
            r => r.GetString(0),
            cancellationToken)]);

    /// <summary>Switches the session to a database (as <c>USE</c> does).</summary>
    [HttpPost("sessions/{id}/database")]
    [ProducesResponseType<SqlDatabaseRequest>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlDatabaseRequest>> ChangeDatabase(string id, SqlDatabaseRequest request)
    {
        try
        {
            var session = sessions.Get(id, User.GetUserId());
            using var lease = session.TryBeginQuery(HttpContext.RequestAborted)
                ?? throw new SessionException(SessionErrorCodes.Busy, "A query is running in this session. Change the database when it's done.");
            await session.Connection.ChangeDatabaseAsync(request.Name, lease.Token);
            return new SqlDatabaseRequest { Name = session.Connection.Database };
        }
        catch (SqlException ex)
        {
            return this.SessionFailure(new SessionException(SessionErrorCodes.SqlError, ex.Message));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    [HttpPost("sessions/{id}/explore")]
    [ProducesResponseType<IReadOnlyList<SqlObjectNode>>(StatusCodes.Status200OK)]
    public Task<ActionResult<IReadOnlyList<SqlObjectNode>>> Explore(string id, SqlExploreRequest request, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => SqlObjectExplorer.ChildrenAsync(connection, request.Node, cancellationToken));

    [HttpPost("sessions/{id}/script")]
    [ProducesResponseType<SqlScriptResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlScriptResponse>> Script(string id, SqlScriptRequest request, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, async connection => new SqlScriptResponse(await SqlScripter.ScriptAsync(connection, request.Node, request.Action, cancellationToken)));

    [HttpGet("sessions/{id}/blocking")]
    [ProducesResponseType<BlockingReport>(StatusCodes.Status200OK)]
    public Task<ActionResult<BlockingReport>> Blocking(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => SqlBlocking.ReadAsync(connection, timeProvider.GetUtcNow(), cancellationToken));

    /// <summary>Ends another session on the server (KILL), rolling back its open transaction. Audited.</summary>
    [HttpPost("sessions/{id}/kill")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Kill(string id, SqlKillRequest request, CancellationToken cancellationToken)
    {
        var session = (SqlServerSession?)null;
        var result = await WithToolConnectionAsync<bool>(id, async connection =>
        {
            session = sessions.Get(id, User.GetUserId());
            await SqlBlocking.KillAsync(connection, request.SessionId, cancellationToken);
            return true;
        });
        if (result.Result is not null)
        {
            return result.Result;
        }

        await audit.RecordAsync(new AuditEntry("mssql.kill", session!.HostId, $"KILL {request.SessionId} · {session.Display}"));
        return NoContent();
    }

    /// <summary>Database users whose login is missing or no longer has their SID (e.g. after a restore), and databases without an owner.</summary>
    [HttpGet("sessions/{id}/orphans")]
    [ProducesResponseType<OrphanReport>(StatusCodes.Status200OK)]
    public Task<ActionResult<OrphanReport>> Orphans(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => SqlOrphans.ReadAsync(connection, cancellationToken));

    /// <summary>Fixes one orphaned user (map, create its login, drop) or a missing owner, then lists what's left. Audited.</summary>
    [HttpPost("sessions/{id}/orphans/fix")]
    [ProducesResponseType<SqlOrphanFixResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlOrphanFixResponse>> FixOrphan(string id, SqlOrphanFix fix, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, async connection =>
        {
            var done = await SqlOrphans.FixAsync(connection, fix, cancellationToken);
            await AuditFixAsync(id, done);
            return new SqlOrphanFixResponse(await SqlOrphans.ReadAsync(connection, cancellationToken), done + ".");
        });

    /// <summary>Maps every orphaned user that has a login of its own name to it (the usual repair after a restore). Audited.</summary>
    [HttpPost("sessions/{id}/orphans/fix-matched")]
    [ProducesResponseType<SqlOrphanFixResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlOrphanFixResponse>> FixMatchedOrphans(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, async connection =>
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
        var session = sessions.Get(id, User.GetUserId());
        return audit.RecordAsync(new AuditEntry("mssql.login_fix", session.HostId, $"{detail} · {session.Display}"));
    }

    /// <summary>Runs on a short connection of the session's own (so it works while a query runs); SQL Server's errors are passed on.</summary>
    private async Task<ActionResult<T>> WithToolConnectionAsync<T>(string id, Func<SqlConnection, Task<T>> action)
    {
        try
        {
            var session = sessions.Get(id, User.GetUserId());
            await using var connection = await session.OpenToolConnectionAsync(HttpContext.RequestAborted);
            return await action(connection);
        }
        catch (SqlException ex)
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

    private IActionResult Run(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }
}
