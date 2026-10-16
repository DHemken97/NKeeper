using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Rdp;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;

namespace NKeeper.Api.Controllers;

/// <summary>
/// Interactive remote sessions over WebSockets. See <c>RemoteSessions/SessionProtocol.cs</c> for
/// the message protocol. Both require the session cookie and a same-site Origin.
/// </summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class RemoteSessionsController(SessionLimiter limiter, IOptions<CorsSettings> cors) : ControllerBase
{
    [HttpGet("api/ssh/session")]
    public Task<IActionResult> Ssh(CancellationToken cancellationToken) =>
        RunAsync((socket, userId) => Session<SshTerminalSession>().RunAsync(socket, userId, cancellationToken), cancellationToken);

    [HttpGet("api/rdp/session")]
    public Task<IActionResult> Rdp(CancellationToken cancellationToken) =>
        RunAsync((socket, userId) => Session<RdpSession>().RunAsync(socket, userId, cancellationToken), cancellationToken);

    private T Session<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    private async Task<IActionResult> RunAsync(Func<SessionSocket, Guid, Task> run, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This endpoint only accepts WebSocket connections.");
        }

        if (!WebSocketOrigin.IsAllowed(Request, cors.Value))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Remote sessions can only be opened from NKeeper itself.");
        }

        var userId = User.GetUserId();
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var socket = new SessionSocket(webSocket);

        // Rejections happen after the upgrade: browsers hide HTTP errors from WebSocket clients.
        if (!limiter.TryAcquire(userId))
        {
            await socket.SendErrorAndCloseAsync(
                SessionErrorCodes.TooManySessions,
                $"You already have {SessionLimiter.MaxSessionsPerUser} open sessions. Close one and try again.",
                cancellationToken);
            return new EmptyResult();
        }

        try
        {
            await run(socket, userId);
        }
        finally
        {
            limiter.Release(userId);
        }

        return new EmptyResult();
    }
}
