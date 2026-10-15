using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;

namespace NKeeper.Api.Controllers;

[ApiController]
[Route("api/ssh")]
[Authorize]
public sealed class SshController(SshTerminalSession session, SshSessionLimiter limiter, IOptions<CorsSettings> cors) : ControllerBase
{
    /// <summary>
    /// Opens an interactive SSH terminal over a WebSocket. See <c>SshProtocol.cs</c> for the
    /// message protocol. Requires the session cookie and a same-site Origin.
    /// </summary>
    [HttpGet("session")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This endpoint only accepts WebSocket connections.");
        }

        if (!WebSocketOrigin.IsAllowed(Request, cors.Value))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Terminal sessions can only be opened from NKeeper itself.");
        }

        var userId = User.GetUserId();
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        // Rejections happen after the upgrade: browsers hide HTTP errors from WebSocket clients.
        if (!limiter.TryAcquire(userId))
        {
            await session.RejectAsync(
                socket,
                SshErrorCodes.TooManySessions,
                $"You already have {SshSessionLimiter.MaxSessionsPerUser} open sessions. Close one and try again.",
                cancellationToken);
            return new EmptyResult();
        }

        try
        {
            await session.RunAsync(socket, userId, cancellationToken);
        }
        finally
        {
            limiter.Release(userId);
        }

        return new EmptyResult();
    }
}
