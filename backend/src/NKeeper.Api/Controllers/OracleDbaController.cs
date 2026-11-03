using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Databases;
using NKeeper.Api.Oracle;

namespace NKeeper.Api.Controllers;

/// <summary>Oracle's Management reports (Storage, Backups, Health). They only read: scripts they offer are run by the user.</summary>
[ApiController]
[Authorize]
[Route("api/oracle/sessions/{id}/reports")]
public sealed class OracleDbaController(DatabaseSessionStore sessions, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("{tool:regex(^(storage|backups|health)$)}")]
    public Task<ActionResult<ToolReport>> Report(string id, string tool, CancellationToken cancellationToken) =>
        DatabaseController.WithToolConnectionAsync(this, sessions, DatabaseEngine.Oracle, id, connection =>
            OracleDba.ReportAsync(connection, tool, timeProvider.GetUtcNow(), cancellationToken));
}
