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

/// <summary>Resuming a Replication Server's suspended connections (audited). Its reports are DatabaseController's.</summary>
[ApiController]
[Authorize]
[Route("api/rs/sessions/{id}")]
public sealed class ReplicationController(DatabaseSessionStore sessions, AuditLog audit) : ControllerBase
{
    private const DatabaseEngine Engine = DatabaseEngine.RepServer;

    [HttpPost("replication/resume")]
    public async Task<IActionResult> Resume(string id, ReplicationResumeRequest request, CancellationToken cancellationToken)
    {
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
