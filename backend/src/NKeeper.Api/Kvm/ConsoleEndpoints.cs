using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using NKeeper.Api.Proxmox;
using NKeeper.Api.RemoteSessions;
using Renci.SshNet;

namespace NKeeper.Api.Kvm;

/// <summary>One connection to a VM's display: what the VM sends, and where to write what the browser sends.</summary>
internal sealed class ConsoleChannel(Stream fromVm, Stream toVm, Func<ValueTask> close) : IAsyncDisposable
{
    public Stream FromVm { get; } = fromVm;

    public Stream ToVm { get; } = toVm;

    public ValueTask DisposeAsync() => close();
}

/// <summary>
/// How a console reaches the VM's display, over a connection to the hypervisor that it owns (SSH,
/// or Proxmox's API). Each channel the browser opens gets its own connection.
/// </summary>
internal abstract class ConsoleEndpoint : IDisposable
{
    public abstract Task<ConsoleChannel> OpenChannelAsync(CancellationToken cancellationToken);

    public abstract void Dispose();
}

/// <summary>A console reached over its SSH connection to the hypervisor.</summary>
internal abstract class SshConsoleEndpoint(SshClient client) : ConsoleEndpoint
{
    protected SshClient Client { get; } = client;

    public override void Dispose() => Client.Dispose();
}

/// <summary>libvirt: a tunnel to the display's TCP port, bound on the API's loopback.</summary>
internal sealed class TunnelEndpoint : SshConsoleEndpoint
{
    private readonly ForwardedPortLocal _tunnel;

    public TunnelEndpoint(SshClient client, ConsoleDisplay display)
        : base(client)
    {
        _tunnel = new ForwardedPortLocal("127.0.0.1", 0, display.Host, (uint)display.Port);
        Client.AddForwardedPort(_tunnel);
        _tunnel.Start();
    }

    public override async Task<ConsoleChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, (int)_tunnel.BoundPort, cancellationToken);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var stream = tcp.GetStream();
        return new ConsoleChannel(stream, stream, () =>
        {
            tcp.Dispose();
            return ValueTask.CompletedTask;
        });
    }

    public override void Dispose()
    {
        try
        {
            _tunnel.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        _tunnel.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Proxmox: a command whose stdin and stdout are the display (<c>qm vncproxy</c>), started once per
/// channel. <paramref name="firstLine"/> is written to it before the browser's data (the VNC ticket),
/// so it never appears on a command line.
/// </summary>
internal sealed class CommandEndpoint(SshClient client, string command, string firstLine, ILogger logger) : SshConsoleEndpoint(client)
{
    public override async Task<ConsoleChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var ssh = Client.CreateCommand(command);
        Task running;
        Stream input;
        try
        {
            running = ssh.ExecuteAsync(CancellationToken.None);
            input = ssh.CreateInputStream();
            await input.WriteAsync(Encoding.UTF8.GetBytes(firstLine + "\n"), cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        catch
        {
            ssh.Dispose();
            throw;
        }

        return new ConsoleChannel(ssh.OutputStream, input, async () =>
        {
            // End of input makes the proxy exit, which ends its output.
            try
            {
                await input.DisposeAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
#pragma warning disable CA1031 // The channel is closing either way; a failure to close it cleanly changes nothing.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            if (running.IsCompletedSuccessfully && ssh.ExitStatus is { } status and not 0)
            {
                logger.LogWarning("Console proxy exited with {ExitStatus}: {Error}", status, ssh.Error.Trim());
            }

            ssh.Dispose();
        });
    }
}

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
