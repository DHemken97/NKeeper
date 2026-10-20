using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Api.Configuration;
using NKeeper.Api.Desktop;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Rpc;
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
        RunAsync((socket, user, token) => Session<SshTerminalSession>().RunAsync(socket, user, token), cancellationToken);

    [HttpGet("api/telnet/session")]
    public Task<IActionResult> Telnet(CancellationToken cancellationToken) =>
        RunAsync((socket, user, token) => Session<NKeeper.Api.Telnet.TelnetTerminalSession>().RunAsync(socket, user, token), cancellationToken);

    [HttpGet("api/rdp/session")]
    public Task<IActionResult> Rdp(CancellationToken cancellationToken) =>
        RunAsync((socket, user, token) => Session<DesktopSession>().RunAsync(socket, user, ServiceProtocol.Rdp, token), cancellationToken);

    [HttpGet("api/rpc/session")]
    public Task<IActionResult> Rpc(CancellationToken cancellationToken) =>
        RunAsync((socket, user, token) => Session<RpcSession>().RunAsync(socket, user, token), cancellationToken);

    [HttpGet("api/vnc/session")]
    public Task<IActionResult> Vnc(CancellationToken cancellationToken) =>
        RunAsync((socket, user, token) => Session<DesktopSession>().RunAsync(socket, user, ServiceProtocol.Vnc, token), cancellationToken);

    private T Session<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    private async Task<IActionResult> RunAsync(Func<SessionSocket, RequestUser, CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This endpoint only accepts WebSocket connections.");
        }

        if (!WebSocketOrigin.IsAllowed(Request, cors.Value))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Remote sessions can only be opened from NKeeper itself.");
        }

        var user = RequestUser.From(User) with { SessionKey = CurrentSession.Key(HttpContext) };
        var userId = user.Id;
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var socket = new SessionSocket(webSocket);

        // Rejections happen after the upgrade: browsers hide HTTP errors from WebSocket clients.
        using var lease = limiter.TryStart(userId, out var terminated);
        if (lease is null)
        {
            await socket.SendErrorAndCloseAsync(
                SessionErrorCodes.TooManySessions,
                $"You already have {SessionLimiter.MaxSessionsPerUser} open sessions. Close one and try again.",
                cancellationToken);
            return new EmptyResult();
        }

        // Ends the session when the browser leaves or an administrator revokes the account.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, terminated);
        await run(socket, user, lifetime.Token);
        return new EmptyResult();
    }
}
