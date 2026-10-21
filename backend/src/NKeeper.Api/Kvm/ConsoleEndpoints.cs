using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// How a console reaches the VM's display on the hypervisor, over the console's SSH connection
/// (which it owns). Each channel the browser opens gets its own connection.
/// </summary>
internal abstract class ConsoleEndpoint(SshClient client) : IDisposable
{
    protected SshClient Client { get; } = client;

    public abstract Task<ConsoleChannel> OpenChannelAsync(CancellationToken cancellationToken);

    public virtual void Dispose() => Client.Dispose();
}

/// <summary>libvirt: a tunnel to the display's TCP port, bound on the API's loopback.</summary>
internal sealed class TunnelEndpoint : ConsoleEndpoint
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
internal sealed class CommandEndpoint(SshClient client, string command, string firstLine, ILogger logger) : ConsoleEndpoint(client)
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
