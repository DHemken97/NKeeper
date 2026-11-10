using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Databases;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.SqlServer;
using NKeeper.Api.Web;

namespace NKeeper.Api.Controllers;

/// <summary>Either the open session (with what the server says about itself), or its certificate to trust first.</summary>
/// <param name="Renewed">The certificate replaces a trusted one: SQL Server made itself a new self-signed certificate (it does at every start).</param>
public sealed record DatabaseOpenResponse(
    string Status, string? SessionId, string Target, DatabaseServerInfo? Server, string? Database, WebCertificateResponse? Certificate, bool Renewed = false);

/// <summary>One of the user's open sessions, to pick another server to compare with or copy to.</summary>
public sealed record DatabaseSessionSummary(string Id, string Display, Guid HostId, string ServerName, string Version);

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
    [Range(1, int.MaxValue, ErrorMessage = "Choose a session.")]
    public int SessionId { get; init; }

    /// <summary>Oracle: the session's SERIAL#.</summary>
    public int? Serial { get; init; }
}

public sealed record SqlScriptResponse(string Sql);

/// <summary>
/// Databases (SQL Server at <c>/api/mssql</c>, Oracle at <c>/api/oracle</c>, SAP ASE at <c>/api/ase</c>),
/// like a small SSMS: query windows on a session of their own (results as grids or text, with
/// messages), an object explorer, scripts of objects, and the blocking manager (blocking chains under
/// their lead blockers, and kill). Each session lives on the server until its tab closes; only the
/// user who opened it can use it.
/// </summary>
[ApiController]
[Authorize]
[Route("api/{engine:regex(" + DatabaseEngines.RoutePattern + ")}")]
public sealed class DatabaseController(DatabaseSessionStore sessions, AuditLog audit, TimeProvider timeProvider) : ControllerBase
{
    private DatabaseEngine Engine => DatabaseEngines.FromRoute(RouteData.Values["engine"] as string) ?? DatabaseEngine.SqlServer;

    private IDatabaseDialect Dialect => DatabaseEngines.Dialect(Engine);

    /// <summary>The user's open sessions of this engine, one per server and login (the oldest: usually the tab's own).</summary>
    [HttpGet("sessions")]
    public IReadOnlyList<DatabaseSessionSummary> Sessions() =>
        [.. sessions.ListFor(User.GetUserId(), Engine)
            .GroupBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(s => new DatabaseSessionSummary(s.Id, s.Display, s.HostId, s.Server.ServerName, s.Server.ProductVersion))];

    [HttpPost("sessions")]
    [ProducesResponseType<DatabaseOpenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DatabaseOpenResponse>> Open(DatabaseOpenRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var connector = HttpContext.RequestServices.GetServices<IDatabaseConnector>().First(c => c.Engine == Engine);
            var result = await connector.OpenAsync(request, RequestUser.From(User), cancellationToken);
            if (result.Prompt is { } certificate)
            {
                return new DatabaseOpenResponse(
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
                    SessionErrorCodes.TooManySessions, $"You already have {DatabaseSessionStore.MaxSessionsPerUser} database sessions open. Close one and try again.");
            }

            string database;
            try
            {
                database = await Dialect.CurrentDatabaseAsync(session.Connection, cancellationToken);
            }
            catch (Exception ex) when (DbErrors.Is(ex))
            {
                database = string.Empty;
            }

            return new DatabaseOpenResponse("ready", session.Id, result.Target, session.Server, database, null);
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
            sessions.Get(id, User.GetUserId(), Engine);
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
        sessions.Get(id, User.GetUserId(), Engine);
        return NoContent();
    });

    [HttpPost("sessions/{id}/query")]
    [ProducesResponseType<SqlQueryResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlQueryResult>> Query(string id, SqlQueryRequest request)
    {
        try
        {
            var session = sessions.Get(id, User.GetUserId(), Engine);
            using var lease = session.TryBeginQuery(HttpContext.RequestAborted)
                ?? throw new SessionException(SessionErrorCodes.Busy, "A query is already running in this session. Wait for it, or cancel it.");
            var result = await Dialect.RunAsync(session.Connection, request.Sql, request.LineOffset, lease.Token);
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
        sessions.Get(id, User.GetUserId(), Engine).Cancel();
        return NoContent();
    });

    /// <summary>The databases (Oracle: schemas) to choose from, for the toolbar's list.</summary>
    [HttpGet("sessions/{id}/databases")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public Task<ActionResult<IReadOnlyList<string>>> Databases(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => Dialect.DatabasesAsync(connection, cancellationToken));

    /// <summary>Switches the session to a database (as <c>USE</c> does; Oracle: its current schema).</summary>
    [HttpPost("sessions/{id}/database")]
    [ProducesResponseType<SqlDatabaseRequest>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SqlDatabaseRequest>> ChangeDatabase(string id, SqlDatabaseRequest request)
    {
        try
        {
            var session = sessions.Get(id, User.GetUserId(), Engine);
            using var lease = session.TryBeginQuery(HttpContext.RequestAborted)
                ?? throw new SessionException(SessionErrorCodes.Busy, "A query is running in this session. Change the database when it's done.");
            return new SqlDatabaseRequest { Name = await Dialect.ChangeDatabaseAsync(session.Connection, request.Name, lease.Token) };
        }
        catch (Exception ex) when (DbErrors.Is(ex))
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
        WithToolConnectionAsync(id, connection => Dialect.ChildrenAsync(connection, request.Node, cancellationToken));

    [HttpPost("sessions/{id}/script")]
    [ProducesResponseType<SqlScriptResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SqlScriptResponse>> Script(string id, SqlScriptRequest request, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, async connection => new SqlScriptResponse(await Dialect.ScriptAsync(connection, request.Node, request.Action, cancellationToken)));

    [HttpGet("sessions/{id}/blocking")]
    [ProducesResponseType<BlockingReport>(StatusCodes.Status200OK)]
    public Task<ActionResult<BlockingReport>> Blocking(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => Dialect.BlockingAsync(connection, timeProvider.GetUtcNow(), cancellationToken));

    /// <summary>A Management report (Storage, Space, Replication Health…), by the tool name the explorer gives it.</summary>
    [HttpGet("sessions/{id}/reports/{tool:minlength(1):maxlength(32)}")]
    public Task<ActionResult<ToolReport>> Report(string id, string tool, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => DatabaseReports.RunAsync(Engine, tool, connection, timeProvider.GetUtcNow(), cancellationToken));

    /// <summary>
    /// What the server says for the insurance questionnaire: its software, databases, who can change data,
    /// auditing, routine deletions and backups. Only reads; it can take a minute on a server with many databases.
    /// </summary>
    [HttpGet("sessions/{id}/survey")]
    [SystemAdministratorOnly]
    [ProducesResponseType<DatabaseSurvey>(StatusCodes.Status200OK)]
    public Task<ActionResult<DatabaseSurvey>> Survey(string id, CancellationToken cancellationToken) =>
        WithToolConnectionAsync(id, connection => DatabaseSurveys.RunAsync(Engine, connection, timeProvider.GetUtcNow(), cancellationToken));

    /// <summary>Ends another session on the server, rolling back its open transaction. Audited.</summary>
    [HttpPost("sessions/{id}/kill")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Kill(string id, SqlKillRequest request, CancellationToken cancellationToken)
    {
        var result = await WithToolConnectionAsync<bool>(id, async connection =>
        {
            await Dialect.KillAsync(connection, request.SessionId, request.Serial, cancellationToken);
            return true;
        });
        if (result.Result is not null)
        {
            return result.Result;
        }

        var session = sessions.Get(id, User.GetUserId(), Engine);
        var what = request.Serial is { } serial ? $"{request.SessionId},{serial}" : $"{request.SessionId}";
        await audit.RecordAsync(new AuditEntry($"{DatabaseEngines.Route(Engine)}.kill", session.HostId, $"KILL {what} · {session.Display}"));
        return NoContent();
    }

    /// <summary>Runs on a short connection of the session's own (so it works while a query runs); the server's errors are passed on.</summary>
    internal static async Task<ActionResult<T>> WithToolConnectionAsync<T>(
        ControllerBase controller, DatabaseSessionStore sessions, DatabaseEngine engine, string id, Func<DbConnection, Task<T>> action)
    {
        try
        {
            var session = sessions.Get(id, controller.User.GetUserId(), engine);
            await using var connection = await session.OpenToolConnectionAsync(controller.HttpContext.RequestAborted);
            return await action(connection);
        }
        catch (Exception ex) when (DbErrors.Is(ex))
        {
            return controller.SessionFailure(new SessionException(SessionErrorCodes.SqlError, ex.Message));
        }
        catch (SqlExplorerException ex)
        {
            return controller.SessionFailure(new SessionException(SessionErrorCodes.BadRequest, ex.Message));
        }
        catch (SessionException ex)
        {
            return controller.SessionFailure(ex);
        }
    }

    private Task<ActionResult<T>> WithToolConnectionAsync<T>(string id, Func<DbConnection, Task<T>> action) =>
        WithToolConnectionAsync(this, sessions, Engine, id, action);

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
