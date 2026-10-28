using System.Net.WebSockets;
using NKeeper.Sdk.Consoles;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Api.Proxmox;

/// <summary>
/// Proxmox's API: the WebSocket of a VNC proxy the node started for this console. Each proxy takes
/// one connection, so a console has one channel; reconnecting opens a new console.
/// </summary>
internal sealed class ProxmoxEndpoint(ProxmoxApi api, JumpTunnel tunnel, ProxmoxGuest guest, VncProxyTicket proxy) : ConsoleEndpoint
{
    public override async Task<ConsoleChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var socket = await api.ConnectVncAsync(guest, proxy, cancellationToken);
        // Owns the socket: closing waits briefly for Proxmox to close its side.
        var stream = WebSocketStream.Create(socket, WebSocketMessageType.Binary, closeTimeout: TimeSpan.FromSeconds(2));
        return new ConsoleChannel(stream, stream, () => stream.DisposeAsync());
    }

    public override void Dispose()
    {
        api.Dispose();
        tunnel.Dispose();
    }
}
