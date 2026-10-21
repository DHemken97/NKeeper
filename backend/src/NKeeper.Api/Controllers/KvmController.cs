using System.ComponentModel.DataAnnotations;
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
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Controllers;

/// <summary>A KVM host request: which host/service/credential, and optionally a VM and a host key to trust.</summary>
public sealed record KvmRequest : SessionStartMessage
{
    /// <summary>The SSH host key fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>For consoles: the VM's libvirt UUID or Proxmox VM ID.</summary>
    [StringLength(64)]
    public string? Vm { get; init; }
}

public sealed record HostKeyPromptResponse(string Algorithm, string Fingerprint);

public sealed record VirtualMachinesResponse(string Status, IReadOnlyList<VirtualMachine>? Vms, HostKeyPromptResponse? HostKey);

public sealed record ConsoleSessionResponse(string Status, string? SessionId, string? Password, HostKeyPromptResponse? HostKey);

/// <summary>
/// KVM hosts managed with libvirt, over SSH like virt-manager: list virtual machines and open
/// their SPICE or VNC consoles, tunnelled through the SSH connection (the console ports usually
/// listen on the hypervisor's loopback only). Proxmox VE nodes are detected and handled the way
/// Proxmox's own console does it (VNC through <c>qm vncproxy</c>). The browser never connects to
/// the hypervisor.
/// </summary>
/// <remarks>
/// VNC consoles live under /api/vnc/, which reverse proxies already treat as WebSocket traffic
/// (it is also where guacd's VNC sessions are).
/// </remarks>
[ApiController]
[Authorize]
public sealed class KvmController(ConsoleSessionStore consoles, IOptions<CorsSettings> cors, TimeProvider timeProvider, ILogger<KvmController> logger) : ControllerBase
{
    private const int DefaultSshPort = 22;

    [HttpPost("api/kvm/vms")]
    [ProducesResponseType<VirtualMachinesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<VirtualMachinesResponse>> ListVirtualMachines(KvmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            return new VirtualMachinesResponse("ready", (await KvmHost.ListAsync(client, cancellationToken)).Vms, null);
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
    [ProducesResponseType<ConsoleSessionResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ConsoleSessionResponse>> OpenSpiceConsole(KvmRequest request, CancellationToken cancellationToken) =>
        OpenConsoleAsync(request, ConsoleProtocols.Spice, cancellationToken);

    [HttpDelete("api/spice/sessions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CloseSpiceConsole(string id) => CloseConsole(id, ConsoleProtocols.Spice);

    /// <summary>One SPICE channel (main, display, inputs, cursor, playback, …) through the console's tunnel.</summary>
    [HttpGet("api/spice/channel/{id}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<IActionResult> SpiceChannel(string id, CancellationToken cancellationToken) =>
        ChannelAsync(id, ConsoleProtocols.Spice, cancellationToken);

    /// <summary>Opens a VM's VNC console; the browser (noVNC) then connects to /api/vnc/channel/{id}.</summary>
    [HttpPost("api/vnc/consoles")]
    [ProducesResponseType<ConsoleSessionResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ConsoleSessionResponse>> OpenVncConsole(KvmRequest request, CancellationToken cancellationToken) =>
        OpenConsoleAsync(request, ConsoleProtocols.Vnc, cancellationToken);

    [HttpDelete("api/vnc/consoles/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CloseVncConsole(string id) => CloseConsole(id, ConsoleProtocols.Vnc);

    /// <summary>The VNC (RFB) connection through the console's tunnel.</summary>
    [HttpGet("api/vnc/channel/{id}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<IActionResult> VncChannel(string id, CancellationToken cancellationToken) =>
        ChannelAsync(id, ConsoleProtocols.Vnc, cancellationToken);

    private async Task<ActionResult<ConsoleSessionResponse>> OpenConsoleAsync(KvmRequest request, string protocol, CancellationToken cancellationToken)
    {
        if (request.Vm is not { Length: > 0 } vm)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Choose a virtual machine.");
        }

        SshClient? client = null;
        ConsoleEndpoint? endpoint = null;
        try
        {
            client = await ConnectAsync(request, cancellationToken);
            var listing = await KvmHost.ListAsync(client, cancellationToken);
            var machine = listing.Vms.FirstOrDefault(v => string.Equals(v.Id, vm, StringComparison.OrdinalIgnoreCase))
                ?? throw new SessionException(
                    SessionErrorCodes.HostNotFound,
                    listing.ProxmoxNode is null
                        ? "This virtual machine no longer exists."
                        : $"There's no virtual machine {vm} in this Proxmox cluster. Containers have no VNC console: open a terminal on the node and use “pct enter {vm}”.");
            if (!string.Equals(machine.State, "running", StringComparison.Ordinal))
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed, $"“{machine.Name}” is not running ({machine.State}). Start it, then open its console.");
            }

            if (machine.Display != protocol)
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed, $"“{machine.Name}” has no {ConsoleProtocols.DisplayName(protocol)} display.");
            }

            string? password;
            if (listing.ProxmoxNode is { } node)
            {
                var command = await ProxmoxHost.VncProxyCommandAsync(client, node, machine, cancellationToken);
                password = ProxmoxHost.NewTicket();
                endpoint = new CommandEndpoint(client, command, password, logger);
            }
            else
            {
                var display = await KvmHost.GetConsoleDisplayAsync(client, Guid.Parse(machine.Id), protocol, cancellationToken);
                password = display.Password;
                endpoint = new TunnelEndpoint(client, display);
            }

            client = null; // Owned by the endpoint now.
            var session = consoles.TryCreate(User.GetUserId(), request.HostId, machine.Name, protocol, password, endpoint);
            if (session is null)
            {
                return Failure(new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {ConsoleSessionStore.MaxSessionsPerUser} open VM consoles. Close one and try again."));
            }

            endpoint = null; // Owned by the session now.
            return new ConsoleSessionResponse("ready", session.Id, session.Password, null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new ConsoleSessionResponse("trustRequired", null, null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return Failure(ex);
        }
        finally
        {
            endpoint?.Dispose();
            client?.Dispose();
        }
    }

    private IActionResult CloseConsole(string id, string protocol) =>
        consoles.EndOwned(id, User.GetUserId(), protocol) ? NoContent() : NotFound();

    private async Task<IActionResult> ChannelAsync(string id, string protocol, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This endpoint only accepts WebSocket connections.");
        }

        if (!WebSocketOrigin.IsAllowed(Request, cors.Value))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Consoles can only be opened from NKeeper itself.");
        }

        if (consoles.Find(id) is not { } session || session.UserId != User.GetUserId() || session.Protocol != protocol)
        {
            return NotFound();
        }

        // spice-html5 (and noVNC, when configured to) asks for the "binary" subprotocol; browsers
        // reject an upgrade that ignores it.
        var subProtocol = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains("binary") ? "binary" : null;
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(subProtocol);
        ConsoleChannel channel;
        try
        {
            channel = await session.Endpoint.OpenChannelAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or SshException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Couldn't open a channel of console {SessionId}", id);
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Console closed", CancellationToken.None);
            return new EmptyResult();
        }

        session.ChannelOpened();
        try
        {
            await RelayAsync(socket, channel, cancellationToken);
        }
        finally
        {
            session.ChannelClosed(timeProvider.GetUtcNow());
        }

        return new EmptyResult();
    }

    /// <summary>Copies bytes both ways until either side closes, then closes the channel.</summary>
    private static async Task RelayAsync(WebSocket socket, ConsoleChannel channel, CancellationToken cancellationToken)
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

                await channel.ToVm.WriteAsync(buffer.AsMemory(0, result.Count), stop.Token);
                await channel.ToVm.FlushAsync(stop.Token);
            }
        }

        async Task FromVmAsync()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await channel.FromVm.ReadAsync(buffer, stop.Token);
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

        // Closing the channel also ends reads that can't be cancelled (a command's output).
        await channel.DisposeAsync();
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
        return await SshCommandConnection.ConnectAsync(target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
    }

    private ObjectResult Failure(SessionException ex) => this.SessionFailure(ex);
}
