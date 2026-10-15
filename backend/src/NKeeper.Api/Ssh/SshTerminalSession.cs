using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace NKeeper.Api.Ssh;

/// <summary>
/// Bridges one browser WebSocket to one interactive SSH shell. Only hosts, services and
/// credentials from the inventory can be used, and decrypted secrets never leave the server.
/// Unknown host keys must be confirmed by the user; changed keys are refused.
/// </summary>
public sealed class SshTerminalSession(
    NKeeperDbContext db,
    CredentialProtector protector,
    TimeProvider timeProvider,
    ILogger<SshTerminalSession> logger) : IDisposable
{
    private const int DefaultSshPort = 22;
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HostKeyDecisionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket _socket = null!;

    public void Dispose() => _sendLock.Dispose();

    /// <summary>Tells the client why no session can be opened, then closes the socket.</summary>
    public async Task RejectAsync(WebSocket socket, string code, string message, CancellationToken cancellationToken)
    {
        _socket = socket;
        await SendErrorAndCloseAsync(code, message, cancellationToken);
    }

    public async Task RunAsync(WebSocket socket, Guid userId, CancellationToken cancellationToken)
    {
        _socket = socket;
        var started = Stopwatch.StartNew();
        Target? target = null;

        try
        {
            var start = await ReceiveStartAsync(cancellationToken);
            target = await ResolveTargetAsync(start, cancellationToken);
            logger.LogInformation(
                "SSH session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                userId, target.HostId, target.Address, target.Port, target.CredentialSource);

            await SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);
            using var client = await ConnectAsync(target, userId, cancellationToken);
            using var shell = client.CreateShellStream("xterm-256color", (uint)start.Cols, (uint)start.Rows, 0, 0, 32 * 1024);
            await SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);
            logger.LogInformation("SSH session connected for user {UserId} to host {HostId}", userId, target.HostId);

            await PumpAsync(client, shell, cancellationToken);
        }
        catch (SshSessionException ex)
        {
            await SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or the request was aborted; nothing left to tell anyone.
        }
        finally
        {
            target?.Dispose();
            logger.LogInformation(
                "SSH session ended for user {UserId} (host {HostId}) after {Duration}",
                userId, target?.HostId, started.Elapsed);
        }
    }

    private async Task<SshStartMessage> ReceiveStartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);

        var (type, data) = await ReceiveAsync(timeout.Token);
        if (type != WebSocketMessageType.Text || ReadType(data) != "start")
        {
            throw new SshSessionException(SshErrorCodes.BadRequest, "The session must begin with a start message.");
        }

        SshStartMessage? start;
        try
        {
            start = JsonSerializer.Deserialize<SshStartMessage>(data, SshJson.Options);
        }
        catch (JsonException)
        {
            start = null;
        }

        if (start is null || !Validator.TryValidateObject(start, new ValidationContext(start), null, validateAllProperties: true))
        {
            throw new SshSessionException(SshErrorCodes.BadRequest, "The start message is invalid.");
        }

        return start;
    }

    private async Task<Target> ResolveTargetAsync(SshStartMessage start, CancellationToken cancellationToken)
    {
        var host = await db.Hosts.AsNoTracking()
            .Include(h => h.Services)
            .Include(h => h.Credentials)
            .Include(h => h.SshHostKeys)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == start.HostId, cancellationToken)
            ?? throw new SshSessionException(SshErrorCodes.HostNotFound, "This host no longer exists.");

        var service = host.Services.OrderBy(s => s.Position).ElementAtOrDefault(start.ServiceIndex);
        if (service?.Protocol != ServiceProtocol.Ssh)
        {
            throw new SshSessionException(SshErrorCodes.NotSshService, "The selected service is not an SSH service. Reload the host and try again.");
        }

        var address = host.Hostname ?? host.IpAddress!;
        var port = service.Port ?? DefaultSshPort;

        string? username;
        string? password;
        string? privateKey = null;
        string credentialSource;
        if (start.CredentialId is { } credentialId)
        {
            var credential = host.Credentials.FirstOrDefault(c => c.Id == credentialId)
                ?? throw new SshSessionException(SshErrorCodes.CredentialNotFound, "This credential no longer exists on the host.");
            // Windows OpenSSH accepts DOMAIN\user for domain accounts.
            username = credential.Username is null ? null : credential.Domain is null ? credential.Username : $"{credential.Domain}\\{credential.Username}";
            password = credential.ProtectedPassword is null ? null : protector.Unprotect(credential.ProtectedPassword);
            privateKey = credential.ProtectedPrivateKey is null ? null : protector.Unprotect(credential.ProtectedPrivateKey);
            credentialSource = $"credential {credentialId}";
        }
        else
        {
            username = start.Username?.Trim();
            password = start.Password;
            credentialSource = "one-off credentials";
        }

        if (string.IsNullOrEmpty(username))
        {
            throw new SshSessionException(SshErrorCodes.MissingUsername, "This credential has no username. Edit the host to add one.");
        }

        if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(privateKey))
        {
            throw new SshSessionException(SshErrorCodes.MissingSecret, "This credential has no password or SSH key.");
        }

        return new Target(host.Id, host.Name, address, port, username, password, privateKey, credentialSource, host.SshHostKeys.Where(k => k.Port == port).ToList());
    }

    private async Task<SshClient> ConnectAsync(Target target, Guid userId, CancellationToken cancellationToken)
    {
        var methods = CreateAuthenticationMethods(target);
        target.Disposables.AddRange(methods.OfType<IDisposable>());
        var connectionInfo = new SshConnectionInfo(target.Address, target.Port, target.Username, [.. methods]) { Timeout = ConnectTimeout };

        var client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var hostKey = new HostKeyVerification(this, target, cancellationToken);
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
                HostKeyOutcome.Changed => new SshSessionException(SshErrorCodes.HostKeyChanged, hostKey.ChangedMessage!),
                HostKeyOutcome.Rejected => new SshSessionException(SshErrorCodes.HostKeyRejected, "The host key was not trusted, so the connection was closed."),
                _ => TranslateConnectError(ex, target),
            };
        }
        finally
        {
            // Persist a newly trusted key even when authentication then fails: it identifies the server, not the login.
            if (hostKey.NewlyTrusted is { } trusted)
            {
                await TrustHostKeyAsync(target, trusted.Algorithm, trusted.Fingerprint, userId);
            }
        }
    }

    private static List<AuthenticationMethod> CreateAuthenticationMethods(Target target)
    {
        var methods = new List<AuthenticationMethod>();
        if (target.PrivateKey is not null)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(target.Username, LoadPrivateKey(target.PrivateKey, target.Password)));
        }

        if (target.Password is { Length: > 0 } password)
        {
            methods.Add(new PasswordAuthenticationMethod(target.Username, password));
            // Many servers (e.g. Ubuntu's default) only offer passwords through keyboard-interactive.
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(target.Username);
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
            throw new SshSessionException(
                SshErrorCodes.InvalidPrivateKey,
                "The SSH private key couldn't be read. Check its format, and that the credential's password is the key's passphrase if it has one.");
        }
    }

    private static SshSessionException TranslateConnectError(Exception ex, Target target) => ex switch
    {
        SshAuthenticationException => new(SshErrorCodes.AuthenticationFailed, $"Authentication failed for {target.Username}. Check the username, password or key."),
        SocketException socket => new(SshErrorCodes.ConnectionFailed, $"Couldn't connect to {target.Address}:{target.Port} ({DescribeSocketError(socket.SocketErrorCode)})."),
        SshOperationTimeoutException or OperationCanceledException or TimeoutException =>
            new(SshErrorCodes.Timeout, $"Timed out connecting to {target.Address}:{target.Port}."),
        SshConnectionException connection => new(SshErrorCodes.ConnectionFailed, $"The SSH connection failed: {connection.Message}"),
        _ => new(SshErrorCodes.ConnectionFailed, $"Couldn't open an SSH session to {target.Address}:{target.Port}."),
    };

    private static string DescribeSocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection refused; is SSH running on that port?",
        SocketError.HostNotFound or SocketError.NoData => "the name could not be resolved",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "the host is unreachable",
        SocketError.TimedOut => "timed out",
        _ => error.ToString(),
    };

    private async Task TrustHostKeyAsync(Target target, string algorithm, string fingerprint, Guid userId)
    {
        db.HostSshKeys.Add(new HostSshKey
        {
            HostId = target.HostId,
            Port = target.Port,
            Algorithm = algorithm,
            Fingerprint = fingerprint,
            TrustedAtUtc = timeProvider.GetUtcNow(),
            TrustedByUserId = userId,
        });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation(
                "User {UserId} trusted SSH host key {Algorithm} SHA256:{Fingerprint} for host {HostId} port {Port}",
                userId, algorithm, fingerprint, target.HostId, target.Port);
        }
        catch (DbUpdateException)
        {
            // Another session trusted the same key at the same moment, or the host was deleted.
            db.ChangeTracker.Clear();
        }
    }

    private async Task PumpAsync(SshClient client, ShellStream shell, CancellationToken cancellationToken)
    {
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = PumpOutputAsync(shell, cancellationToken);
        var input = PumpInputAsync(shell, inputCancellation.Token);

        if (await Task.WhenAny(output, input) == output)
        {
            // The remote shell ended: say so, then complete the close handshake with the browser.
            await SendJsonAsync(new { type = "closed", message = "The session ended." }, cancellationToken);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", cancellationToken);
            inputCancellation.CancelAfter(CloseHandshakeTimeout);
        }
        else
        {
            // The browser closed the tab or the socket failed: hang up the SSH side.
            client.Disconnect();
            shell.Dispose();
            if (_socket.State == WebSocketState.CloseReceived)
            {
                // Acknowledge the browser's close frame to complete the handshake.
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session closed", CancellationToken.None);
            }
        }

        await Task.WhenAll(output, input).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task PumpOutputAsync(ShellStream shell, CancellationToken cancellationToken)
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

                await SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, cancellationToken);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed because the browser side closed first.
        }
    }

    private async Task PumpInputAsync(ShellStream shell, CancellationToken cancellationToken)
    {
        while (_socket.State == WebSocketState.Open)
        {
            var (type, data) = await ReceiveAsync(cancellationToken);
            switch (type)
            {
                case WebSocketMessageType.Close:
                    return;
                case WebSocketMessageType.Binary:
                    shell.Write(data, 0, data.Length);
                    shell.Flush();
                    break;
                case WebSocketMessageType.Text when ReadType(data) == "resize":
                    var size = JsonSerializer.Deserialize<ResizeMessage>(data, SshJson.Options);
                    if (size is { Cols: > 0 and <= 1000, Rows: > 0 and <= 1000 })
                    {
                        shell.ChangeWindowSize((uint)size.Cols, (uint)size.Rows, 0, 0);
                    }

                    break;
            }
        }
    }

    private async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new SshSessionException(SshErrorCodes.BadRequest, "Message too large.");
            }

            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }

    private static string? ReadType(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var type)
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SendJsonAsync(object message, CancellationToken cancellationToken) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, SshJson.Options), WebSocketMessageType.Text, cancellationToken);

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(data, type, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendErrorAndCloseAsync(string code, string message, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await SendJsonAsync(new { type = "error", code, message }, cancellationToken);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, code, cancellationToken);
            await AwaitClientCloseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or SshSessionException)
        {
            // The browser is already gone.
        }
    }

    /// <summary>
    /// Completes the close handshake: returning before the browser acknowledges can drop the
    /// messages just sent, so wait (briefly) for its close frame, discarding anything else.
    /// </summary>
    private async Task AwaitClientCloseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CloseHandshakeTimeout);
        while (_socket.State == WebSocketState.CloseSent)
        {
            var (type, _) = await ReceiveAsync(timeout.Token);
            if (type == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private void LogHostKeyChanged(Target target, string trusted, string presented) =>
        logger.LogWarning(
            "SSH host key for host {HostId} port {Port} changed: trusted SHA256:{Trusted}, presented SHA256:{Presented}",
            target.HostId, target.Port, trusted, presented);

    private sealed record ResizeMessage(int Cols, int Rows);

    private sealed record HostKeyDecisionMessage(bool Trust);

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
    private sealed class HostKeyVerification(SshTerminalSession session, Target target, CancellationToken cancellationToken)
    {
        public HostKeyOutcome Outcome { get; private set; }

        public (string Algorithm, string Fingerprint)? NewlyTrusted { get; private set; }

        public string? ChangedMessage { get; private set; }

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
                    session.LogHostKeyChanged(target, known.Fingerprint, fingerprint);
                    ChangedMessage = $"The {algorithm} host key of {target.Address}:{target.Port} has changed since it was trusted. "
                        + "This happens when a server is reinstalled, but it can also mean someone is intercepting the connection. "
                        + "If the change is expected, forget the stored key in the host's details and connect again.";
                    session.SendJsonAsync(
                        new { type = "hostKey", status = "changed", port = target.Port, algorithm, fingerprint, trustedFingerprint = known.Fingerprint },
                        cancellationToken).GetAwaiter().GetResult();
                }

                return;
            }

            e.CanTrust = AskUser(algorithm, fingerprint);
            Outcome = e.CanTrust ? HostKeyOutcome.Trusted : HostKeyOutcome.Rejected;
            NewlyTrusted = e.CanTrust ? (algorithm, fingerprint) : null;
        }

        private bool AskUser(string algorithm, string fingerprint)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HostKeyDecisionTimeout);
            try
            {
                session.SendJsonAsync(new { type = "hostKey", status = "unknown", port = target.Port, algorithm, fingerprint }, timeout.Token)
                    .GetAwaiter().GetResult();
                while (true)
                {
                    var (type, data) = session.ReceiveAsync(timeout.Token).GetAwaiter().GetResult();
                    if (type == WebSocketMessageType.Close)
                    {
                        return false;
                    }

                    if (type == WebSocketMessageType.Text && ReadType(data) == "hostKeyDecision")
                    {
                        return JsonSerializer.Deserialize<HostKeyDecisionMessage>(data, SshJson.Options)?.Trust == true;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or SshSessionException)
            {
                return false;
            }
        }
    }

    /// <summary>Everything needed to connect, including decrypted secrets (kept in memory only).</summary>
    private sealed record Target(
        Guid HostId,
        string HostName,
        string Address,
        int Port,
        string Username,
        string? Password,
        string? PrivateKey,
        string CredentialSource,
        IReadOnlyList<HostSshKey> KnownKeys) : IDisposable
    {
        public List<IDisposable> Disposables { get; } = [];

        public string Display => $"{Username}@{Address}:{Port}";

        public void Dispose() => Disposables.ForEach(d => d.Dispose());
    }
}
