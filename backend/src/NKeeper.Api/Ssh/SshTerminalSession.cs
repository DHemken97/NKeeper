using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace NKeeper.Api.Ssh;

/// <summary>The first message of an SSH terminal session.</summary>
public sealed record SshStartMessage : SessionStartMessage
{
    [Range(1, 1000)]
    public int Cols { get; init; } = 80;

    [Range(1, 1000)]
    public int Rows { get; init; } = 24;
}

/// <summary>
/// Bridges one browser WebSocket to one interactive SSH shell. Only hosts, services and
/// credentials from the inventory can be used, and decrypted secrets never leave the server.
/// Unknown host keys must be confirmed by the user; changed keys are refused.
/// </summary>
internal sealed class SshTerminalSession(SessionTargetResolver resolver, TrustedKeyStore trustedKeys, ILogger<SshTerminalSession> logger)
{
    private const int DefaultSshPort = 22;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HostKeyDecisionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    public async Task RunAsync(SessionSocket socket, Guid userId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        SessionTarget? target = null;
        var disposables = new List<IDisposable>();

        try
        {
            var start = await socket.ReceiveStartAsync<SshStartMessage>(cancellationToken);
            target = await resolver.ResolveAsync(start, ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            if (target.Password is null && target.PrivateKey is null)
            {
                throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password or SSH key.");
            }

            logger.LogInformation(
                "SSH session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                userId, target.HostId, target.Address, target.Port, target.CredentialSource);

            await socket.SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);
            using var client = await ConnectAsync(socket, target, disposables, userId, cancellationToken);
            using var shell = client.CreateShellStream("xterm-256color", (uint)start.Cols, (uint)start.Rows, 0, 0, 32 * 1024);
            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);
            logger.LogInformation("SSH session connected for user {UserId} to host {HostId}", userId, target.HostId);

            await PumpAsync(socket, client, shell, cancellationToken);
        }
        catch (SessionException ex)
        {
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or the request was aborted; nothing left to tell anyone.
        }
        finally
        {
            disposables.ForEach(d => d.Dispose());
            logger.LogInformation("SSH session ended for user {UserId} (host {HostId}) after {Duration}", userId, target?.HostId, started.Elapsed);
        }
    }

    private async Task<SshClient> ConnectAsync(
        SessionSocket socket, SessionTarget target, List<IDisposable> disposables, Guid userId, CancellationToken cancellationToken)
    {
        // Windows OpenSSH accepts DOMAIN\user for domain accounts.
        var username = target.Domain is null ? target.Username : $"{target.Domain}\\{target.Username}";
        var methods = CreateAuthenticationMethods(username, target);
        disposables.AddRange(methods.OfType<IDisposable>());
        var connectionInfo = new SshConnectionInfo(target.Address, target.Port, username, [.. methods]) { Timeout = ConnectTimeout };

        var client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var hostKey = new HostKeyVerification(socket, trustedKeys, target, cancellationToken);
        client.HostKeyReceived += hostKey.OnHostKeyReceived;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout + HostKeyDecisionTimeout);
        try
        {
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw hostKey.Outcome switch
            {
                HostKeyOutcome.Changed => new SessionException(SessionErrorCodes.HostKeyChanged, TrustedKeyStore.ChangedMessage("host key", target)),
                HostKeyOutcome.Rejected => new SessionException(SessionErrorCodes.HostKeyRejected, "The host key was not trusted, so the connection was closed."),
                _ => TranslateConnectError(ex, target, username),
            };
        }
        finally
        {
            // Persist a newly trusted key even when authentication then fails: it identifies the server, not the login.
            if (hostKey.NewlyTrusted is { } trusted)
            {
                await trustedKeys.TrustAsync(target, ServiceProtocol.Ssh, trusted.Algorithm, trusted.Fingerprint, userId);
            }
        }
    }

    private static List<AuthenticationMethod> CreateAuthenticationMethods(string username, SessionTarget target)
    {
        var methods = new List<AuthenticationMethod>();
        if (target.PrivateKey is not null)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(username, LoadPrivateKey(target.PrivateKey, target.Password)));
        }

        if (target.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password));
            // Many servers (e.g. Ubuntu's default) only offer passwords through keyboard-interactive.
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(username);
            keyboardInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(keyboardInteractive);
        }

        return methods;
    }

    /// <summary>Loads a key; the credential's password doubles as the key's passphrase.</summary>
    private static PrivateKeyFile LoadPrivateKey(string privateKey, string? passphrase)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new SessionException(
                SessionErrorCodes.InvalidPrivateKey,
                "The SSH private key couldn't be read. Check its format, and that the credential's password is the key's passphrase if it has one.");
        }
    }

    private static SessionException TranslateConnectError(Exception ex, SessionTarget target, string username) => ex switch
    {
        SshAuthenticationException => new(SessionErrorCodes.AuthenticationFailed, $"Authentication failed for {username}. Check the username, password or key."),
        SocketException socket => new(SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {target.Address}:{target.Port} ({SocketErrors.Describe(socket.SocketErrorCode, "SSH")})."),
        SshOperationTimeoutException or OperationCanceledException or TimeoutException =>
            new(SessionErrorCodes.Timeout, $"Timed out connecting to {target.Address}:{target.Port}."),
        SshConnectionException connection => new(SessionErrorCodes.ConnectionFailed, $"The SSH connection failed: {connection.Message}"),
        _ => new(SessionErrorCodes.ConnectionFailed, $"Couldn't open an SSH session to {target.Address}:{target.Port}."),
    };

    private static async Task PumpAsync(SessionSocket socket, SshClient client, ShellStream shell, CancellationToken cancellationToken)
    {
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = PumpOutputAsync(socket, shell, cancellationToken);
        var input = PumpInputAsync(socket, shell, inputCancellation.Token);

        if (await Task.WhenAny(output, input) == output)
        {
            // The remote shell ended: say so, then complete the close handshake with the browser.
            // The input loop then receives the browser's close acknowledgement and returns.
            await socket.SendJsonAsync(new { type = "closed", message = "The session ended." }, cancellationToken);
            await socket.BeginCloseAsync("Session ended", cancellationToken);
            inputCancellation.CancelAfter(CloseHandshakeTimeout);
        }
        else
        {
            // The browser closed the tab or the socket failed: hang up the SSH side.
            client.Disconnect();
            shell.Dispose();
            await socket.AcknowledgeCloseAsync();
        }

        await Task.WhenAll(output, input).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static async Task PumpOutputAsync(SessionSocket socket, ShellStream shell, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await shell.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, cancellationToken);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed because the browser side closed first.
        }
    }

    private static async Task PumpInputAsync(SessionSocket socket, ShellStream shell, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            var (type, data) = await socket.ReceiveAsync(cancellationToken);
            switch (type)
            {
                case WebSocketMessageType.Close:
                    return;
                case WebSocketMessageType.Binary:
                    shell.Write(data, 0, data.Length);
                    shell.Flush();
                    break;
                case WebSocketMessageType.Text when SessionSocket.ReadType(data) == "resize":
                    var size = JsonSerializer.Deserialize<ResizeMessage>(data, SessionJson.Options);
                    if (size is { Cols: > 0 and <= 1000, Rows: > 0 and <= 1000 })
                    {
                        shell.ChangeWindowSize((uint)size.Cols, (uint)size.Rows, 0, 0);
                    }

                    break;
            }
        }
    }

    private sealed record ResizeMessage(int Cols, int Rows);

    private enum HostKeyOutcome
    {
        Pending,
        Trusted,
        Rejected,
        Changed,
    }

    /// <summary>
    /// Trust on first use. SSH.NET raises HostKeyReceived synchronously during the key exchange,
    /// while <see cref="ConnectAsync"/> is awaited and nothing else reads the socket, so the
    /// handler can ask the browser and block until it answers.
    /// </summary>
    private sealed class HostKeyVerification(SessionSocket socket, TrustedKeyStore trustedKeys, SessionTarget target, CancellationToken cancellationToken)
    {
        public HostKeyOutcome Outcome { get; private set; }

        public (string Algorithm, string Fingerprint)? NewlyTrusted { get; private set; }

        public void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
        {
            var algorithm = e.HostKeyName;
            var fingerprint = e.FingerPrintSHA256;
            var known = target.KnownKeys.FirstOrDefault(k => k.Algorithm == algorithm);

            if (known is not null)
            {
                e.CanTrust = known.Fingerprint == fingerprint;
                Outcome = e.CanTrust ? HostKeyOutcome.Trusted : HostKeyOutcome.Changed;
                if (!e.CanTrust)
                {
                    trustedKeys.LogChanged(target, ServiceProtocol.Ssh, known.Fingerprint, fingerprint);
                    socket.SendJsonAsync(
                        new { type = "hostKey", status = "changed", kind = "ssh", port = target.Port, algorithm, fingerprint, trustedFingerprint = known.Fingerprint },
                        cancellationToken).GetAwaiter().GetResult();
                }

                return;
            }

            e.CanTrust = socket.AskToTrustAsync(
                new { type = "hostKey", status = "unknown", kind = "ssh", port = target.Port, algorithm, fingerprint },
                cancellationToken).GetAwaiter().GetResult();
            Outcome = e.CanTrust ? HostKeyOutcome.Trusted : HostKeyOutcome.Rejected;
            NewlyTrusted = e.CanTrust ? (algorithm, fingerprint) : null;
        }
    }
}
