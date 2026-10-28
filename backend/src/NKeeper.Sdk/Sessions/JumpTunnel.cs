using NKeeper.Sdk.Ssh;
using Renci.SshNet;

namespace NKeeper.Sdk.Sessions;

/// <summary>
/// Where to dial to reach a host: the host itself, or, when it has a jump host, a port on the API's
/// loopback forwarded over SSH through the jump host to it. Dispose to close the tunnel.
/// </summary>
/// <remarks>
/// The jump host's SSH key must already be trusted (an SSH session to it asks): the tunnel can't
/// stop to ask. The target's own identity (host key, certificate) is still checked against the
/// target, not the jump host, so the jump host can't impersonate it.
/// </remarks>
public sealed class JumpTunnel : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly SshClient? _client;
    private readonly ForwardedPortLocal? _forward;
    private readonly IReadOnlyList<IDisposable> _methods;

    private JumpTunnel(string address, int port, SshClient? client = null, ForwardedPortLocal? forward = null, IReadOnlyList<IDisposable>? methods = null)
    {
        Address = address;
        Port = port;
        _client = client;
        _forward = forward;
        _methods = methods ?? [];
    }

    /// <summary>The address to connect to: the host's own, or 127.0.0.1 for a tunnel.</summary>
    public string Address { get; }

    public int Port { get; }

    public bool IsTunnel => _client is not null;

    /// <summary>Opens a tunnel through <paramref name="jump"/> to address:port, or none when there's no jump host.</summary>
    /// <exception cref="SessionException">The jump host can't be reached, isn't trusted, or refused the login or the forward.</exception>
    public static async Task<JumpTunnel> OpenAsync(SessionTarget? jump, string address, int port, CancellationToken cancellationToken)
    {
        if (jump is null)
        {
            return new JumpTunnel(address, port);
        }

        if (jump.Password is null && jump.PrivateKey is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, $"The jump host “{jump.HostName}”'s credential has no password or SSH key.");
        }

        var username = SshAuthentication.UserName(jump);
        var methods = SshAuthentication.CreateMethods(username, jump);
        var client = new SshClient(new Renci.SshNet.ConnectionInfo(jump.Address, jump.Port, username, [.. methods]) { Timeout = ConnectTimeout })
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        var keyProblem = (string?)null;
        client.HostKeyReceived += (_, e) =>
        {
            var known = jump.KnownKeys.FirstOrDefault(k => k.Algorithm == e.HostKeyName);
            e.CanTrust = known is not null && known.Fingerprint == e.FingerPrintSHA256;
            keyProblem = known is null
                ? $"The SSH host key of the jump host “{jump.HostName}” isn't trusted yet. Connect to it over SSH once to check and trust it."
                : e.CanTrust ? null : TrustedKeyStore.ChangedMessage("SSH host key", jump);
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            methods.OfType<IDisposable>().ToList().ForEach(m => m.Dispose());
            if (keyProblem is not null)
            {
                throw new SessionException(SessionErrorCodes.HostKeyRejected, keyProblem);
            }

            var error = SshAuthentication.TranslateConnectError(ex, jump, username);
            throw new SessionException(error.Code, $"Jump host “{jump.HostName}”: {error.Message}");
        }

        try
        {
            var forward = new ForwardedPortLocal("127.0.0.1", 0, address, (uint)port);
            client.AddForwardedPort(forward);
            forward.Start();
            return new JumpTunnel("127.0.0.1", (int)forward.BoundPort, client, forward, [.. methods.OfType<IDisposable>()]);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
        {
            client.Dispose();
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The jump host “{jump.HostName}” wouldn't forward to {address}:{port} ({ex.Message}). It must allow TCP forwarding.");
        }
    }

    /// <summary>A TCP stream to the tunnel's local end, for HTTP clients' ConnectCallback.</summary>
    public static async ValueTask<Stream> ConnectStreamAsync(JumpTunnel tunnel, CancellationToken cancellationToken)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(tunnel.Address, tunnel.Port, cancellationToken);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _forward?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        _forward?.Dispose();
        _client?.Dispose();
        foreach (var method in _methods)
        {
            method.Dispose();
        }
    }
}
