using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Api.Configuration;
using NKeeper.Api.Kvm;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Controllers;

/// <summary>A KVM host request: which host/service/credential, and optionally a VM and a host key to trust.</summary>
public sealed record KvmRequest : SessionStartMessage
{
    /// <summary>The SSH host key fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>For consoles: the VM's libvirt UUID.</summary>
    public Guid? Vm { get; init; }
}

public sealed record HostKeyPromptResponse(string Algorithm, string Fingerprint);

public sealed record VirtualMachinesResponse(string Status, IReadOnlyList<VirtualMachine>? Vms, HostKeyPromptResponse? HostKey);

public sealed record SpiceSessionResponse(string Status, string? SessionId, string? Password, HostKeyPromptResponse? HostKey);

/// <summary>
/// KVM hosts managed with libvirt, over SSH like virt-manager: list virtual machines and open
/// their SPICE consoles, tunnelled through the SSH connection (the console ports usually listen
/// on the hypervisor's loopback only). The browser never connects to the hypervisor.
/// </summary>
[ApiController]
[Authorize]
public sealed class KvmController(SpiceSessionStore consoles, IOptions<CorsSettings> cors, TimeProvider timeProvider) : ControllerBase
{
    private const int DefaultSshPort = 22;

    [HttpPost("api/kvm/vms")]
    [ProducesResponseType<VirtualMachinesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<VirtualMachinesResponse>> ListVirtualMachines(KvmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            return new VirtualMachinesResponse("ready", await KvmHost.ListAsync(client, cancellationToken), null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new VirtualMachinesResponse("trustRequired", null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return Failure(ex);
        }
    }

    /// <summary>Opens a VM's SPICE console; the browser then connects its SPICE channels to /api/spice/channel/{id}.</summary>
    [HttpPost("api/spice/sessions")]
    [ProducesResponseType<SpiceSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SpiceSessionResponse>> OpenConsole(KvmRequest request, CancellationToken cancellationToken)
    {
        if (request.Vm is not { } vm)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Choose a virtual machine.");
        }

        SshClient? client = null;
        try
        {
            client = await ConnectAsync(request, cancellationToken);
            var vms = await KvmHost.ListAsync(client, cancellationToken);
            var machine = vms.FirstOrDefault(v => v.Uuid == vm)
                ?? throw new SessionException(SessionErrorCodes.HostNotFound, "This virtual machine no longer exists.");
            if (!string.Equals(machine.State, "running", StringComparison.Ordinal))
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed, $"“{machine.Name}” is not running ({machine.State}). Start it, then open its console.");
            }

            var display = await KvmHost.GetSpiceDisplayAsync(client, vm, cancellationToken);

            var session = consoles.TryCreate(User.GetUserId(), request.HostId, machine.Name, client, display);
            if (session is null)
            {
                return Failure(new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {SpiceSessionStore.MaxSessionsPerUser} open VM consoles. Close one and try again."));
            }

            client = null; // Owned by the session now.
            return new SpiceSessionResponse("ready", session.Id, session.Password, null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new SpiceSessionResponse("trustRequired", null, null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return Failure(ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    [HttpDelete("api/spice/sessions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CloseConsole(string id) => consoles.EndOwned(id, User.GetUserId()) ? NoContent() : NotFound();

    /// <summary>One SPICE channel (main, display, inputs, cursor, playback, …) through the console's tunnel.</summary>
    [HttpGet("api/spice/channel/{id}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Channel(string id, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This endpoint only accepts WebSocket connections.");
        }

        if (!WebSocketOrigin.IsAllowed(Request, cors.Value))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Consoles can only be opened from NKeeper itself.");
        }

        if (consoles.Find(id) is not { } session || session.UserId != User.GetUserId())
        {
            return NotFound();
        }

        // spice-html5 asks for the "binary" subprotocol; browsers reject an upgrade that ignores it.
        var subProtocol = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains("binary") ? "binary" : null;
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(subProtocol);
        using var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, session.TunnelPort, cancellationToken);
        }
        catch (SocketException)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Console closed", CancellationToken.None);
            return new EmptyResult();
        }

        session.ChannelOpened();
        try
        {
            await RelayAsync(socket, tcp.GetStream(), cancellationToken);
        }
        finally
        {
            session.ChannelClosed(timeProvider.GetUtcNow());
        }

        return new EmptyResult();
    }

    /// <summary>Copies bytes both ways until either side closes.</summary>
    private static async Task RelayAsync(WebSocket socket, NetworkStream stream, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task FromBrowserAsync()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, result.Count), stop.Token);
            }
        }

        async Task FromVmAsync()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, stop.Token);
                if (read == 0)
                {
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, stop.Token);
            }
        }

        var browser = FromBrowserAsync();
        var vm = FromVmAsync();
        await Task.WhenAny(browser, vm);
        await stop.CancelAsync();
        await Task.WhenAll(browser, vm).ContinueWith(_ => { }, TaskScheduler.Default);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Channel closed", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private async Task<SshClient> ConnectAsync(KvmRequest request, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var target = await services.GetRequiredService<SessionTargetResolver>()
            .ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Kvm, DefaultSshPort, cancellationToken);
        return await KvmHost.ConnectAsync(target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
    }

    /// <summary>Session failures as problem details with the session error code (never 401, which would sign the user out).</summary>
    private ObjectResult Failure(SessionException ex)
    {
        var status = ex.Code switch
        {
            SessionErrorCodes.HostNotFound or SessionErrorCodes.CredentialNotFound => StatusCodes.Status404NotFound,
            SessionErrorCodes.HostKeyChanged => StatusCodes.Status409Conflict,
            SessionErrorCodes.TooManySessions => StatusCodes.Status429TooManyRequests,
            SessionErrorCodes.ConnectionFailed or SessionErrorCodes.Timeout => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: ex.Message);
        problem.Extensions["code"] = ex.Code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
