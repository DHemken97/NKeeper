using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Ase;
using NKeeper.Api.Auditing;
using NKeeper.Api.Databases;
using NKeeper.Api.Security;

namespace NKeeper.Api.Controllers;

public sealed record ReplicationResumeRequest
{
    /// <summary>"server.database".</summary>
    [Required]
    [StringLength(261)]
    public string Connection { get; init; } = string.Empty;

    /// <summary>Skip the transaction that failed (Replication Server writes it to the exceptions log).</summary>
    public bool SkipTransaction { get; init; }
}

/// <summary>
/// Replication Health: on an ASE tab, the primary side (logs and Rep Agents); on a Replication Server tab,
/// its threads, queues and stable devices, and resuming suspended connections (audited).
/// </summary>
[ApiController]
[Authorize]
[Route("api/{engine:regex(^(ase|rs)$)}/sessions/{id}")]
public sealed class ReplicationController(DatabaseSessionStore sessions, AuditLog audit, TimeProvider timeProvider) : ControllerBase
{
    private DatabaseEngine Engine => DatabaseEngines.FromRoute(RouteData.Values["engine"] as string) ?? DatabaseEngine.Ase;

    [HttpGet("replication")]
    public Task<ActionResult<ToolReport>> Report(string id, CancellationToken cancellationToken) =>
        DatabaseController.WithToolConnectionAsync(this, sessions, Engine, id, connection =>
            Engine == DatabaseEngine.RepServer
                ? AseReplication.ReplicationServerAsync(connection, timeProvider.GetUtcNow(), cancellationToken)
                : AseReplication.PrimaryAsync(connection, timeProvider.GetUtcNow(), cancellationToken));

    /// <summary>Replication Server's Admin Commands and License reports.</summary>
    [HttpGet("reports/{tool:regex(^(admin|license)$)}")]
    public Task<ActionResult<ToolReport>> RepServerReport(string id, string tool, CancellationToken cancellationToken)
    {
        if (Engine != DatabaseEngine.RepServer)
        {
            // ASE's reports are in AseToolsController (api/ase/sessions/{id}/reports/…).
            return Task.FromResult<ActionResult<ToolReport>>(NotFound());
        }

        return DatabaseController.WithToolConnectionAsync(this, sessions, Engine, id, connection =>
            tool == SybaseAdmin.Admin
                ? SybaseAdmin.AdminAsync(connection, timeProvider.GetUtcNow(), cancellationToken)
                : SybaseAdmin.ReplicationServerLicenseAsync(connection, timeProvider.GetUtcNow(), cancellationToken));
    }

    [HttpPost("replication/resume")]
    public async Task<IActionResult> Resume(string id, ReplicationResumeRequest request, CancellationToken cancellationToken)
    {
        if (Engine != DatabaseEngine.RepServer)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Connections are resumed on the Replication Server.");
        }

        var result = await DatabaseController.WithToolConnectionAsync<bool>(this, sessions, Engine, id, async connection =>
        {
            await AseReplication.ResumeAsync(connection, request.Connection.Trim(), request.SkipTransaction, cancellationToken);
            return true;
        });
        if (result.Result is not null)
        {
            return result.Result;
        }

        var session = sessions.Get(id, User.GetUserId(), Engine);
        var skip = request.SkipTransaction ? " SKIP TRANSACTION" : string.Empty;
        await audit.RecordAsync(new AuditEntry("rs.resume", session.HostId, $"RESUME CONNECTION TO {request.Connection.Trim()}{skip} · {session.Display}"));
        return NoContent();
    }
}
