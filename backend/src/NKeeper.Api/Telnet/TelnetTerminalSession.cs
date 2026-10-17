using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.Access;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Telnet;

/// <summary>The first message of a Telnet terminal session.</summary>
public sealed record TelnetStartMessage : SessionStartMessage
{
    [Range(1, 1000)]
    public int Cols { get; init; } = 80;

    [Range(1, 1000)]
    public int Rows { get; init; } = 24;
}

/// <summary>
/// Bridges one browser WebSocket to one Telnet connection, with the same messages as SSH sessions
/// (raw terminal bytes, "resize", "closed"). Telnet has no login of its own: the server asks in the
/// terminal. With a credential, NKeeper answers the first login and password prompts for the user;
/// without one, the user types them. Telnet isn't encrypted: everything, passwords included, crosses
/// the network from NKeeper to the host in the clear.
/// </summary>
internal sealed partial class TelnetTerminalSession(SessionTargetResolver resolver, Auditing.AuditLog audit, ILogger<TelnetTerminalSession> logger)
{
    private const int DefaultTelnetPort = 23;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Prompts are only answered this soon after connecting, never later in the session.</summary>
    private static readonly TimeSpan AutoLoginWindow = TimeSpan.FromSeconds(30);

    public async Task RunAsync(SessionSocket socket, RequestUser user, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        SessionTarget? target = null;
        Auditing.AuditedSession? audited = null;
        try
        {
            var start = await socket.ReceiveStartAsync<TelnetStartMessage>(cancellationToken);
            audited = audit.BeginSession("Telnet", start.HostId, user, record: true);
            target = await resolver.ResolveAsync(start, user, ServiceProtocol.Telnet, DefaultTelnetPort, cancellationToken, requireUsername: false);
            logger.LogInformation(
                "Telnet session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                user.Id, target.HostId, target.Address, target.Port, target.CredentialSource);

            await socket.SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);
            using var tcp = await ConnectAsync(target, cancellationToken);
            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);

            await audited.ConnectedAsync(target.Display, start.Cols, start.Rows);
            using var connection = new Connection(socket, tcp.GetStream(), target, start.Cols, start.Rows, audited.Recorder);
            await connection.PumpAsync(cancellationToken);
        }
        catch (SessionException ex)
        {
            await (audited?.FailedAsync(ex.Message, target?.Display) ?? Task.CompletedTask);
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or the request was aborted.
        }
        finally
        {
            await (audited?.EndAsync() ?? Task.CompletedTask);
            logger.LogInformation("Telnet session ended for user {UserId} (host {HostId}) after {Duration}", user.Id, target?.HostId, started.Elapsed);
        }
    }

    private static async Task<TcpClient> ConnectAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(target.Address, target.Port, timeout.Token);
            return tcp;
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {target.Address}:{target.Port} ({SocketErrors.Describe(ex.SocketErrorCode, "Telnet")}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new SessionException(SessionErrorCodes.Timeout, $"Timed out connecting to {target.Address}:{target.Port}.");
        }
    }

    // A prompt at the end of what the server sent so far, e.g. "login: " or "Password:".
    [GeneratedRegex(@"(login|username|user name|user)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UsernamePrompt();

    [GeneratedRegex(@"(password|passcode)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();

    /// <summary>One open connection: the two pumps, the protocol state and the automatic login.</summary>
    private sealed class Connection(SessionSocket socket, NetworkStream stream, SessionTarget target, int cols, int rows, Auditing.TerminalRecorder? recorder) : IDisposable
    {
        private readonly TelnetProtocol _protocol = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private readonly StringBuilder _recent = new();
        private bool _sentUsername;
        private bool _sentPassword;

        public async Task PumpAsync(CancellationToken cancellationToken)
        {
            _protocol.Resize(cols, rows);
            using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var output = PumpOutputAsync(cancellationToken);
            var input = PumpInputAsync(inputCancellation.Token);

            if (await Task.WhenAny(output, input) == output)
            {
                // The host closed the connection (e.g. "exit"): say so, then close with the browser.
                await socket.SendJsonAsync(new { type = "closed", message = "The session ended." }, cancellationToken);
                await socket.BeginCloseAsync("Session ended", cancellationToken);
                inputCancellation.CancelAfter(CloseHandshakeTimeout);
            }
            else
            {
                // The browser closed the tab: hang up the Telnet side.
                stream.Socket.Close();
                await socket.AcknowledgeCloseAsync();
            }

            await Task.WhenAll(output, input).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        private async Task PumpOutputAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var data = new List<byte>(buffer.Length);
            var replies = new List<byte>();
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        return;
                    }

                    data.Clear();
                    replies.Clear();
                    _protocol.Receive(buffer.AsSpan(0, read), data, replies);
                    if (replies.Count > 0)
                    {
                        await WriteAsync([.. replies], cancellationToken);
                    }

                    if (data.Count > 0)
                    {
                        var bytes = data.ToArray();
                        recorder?.Output(bytes);
                        await socket.SendAsync(bytes, WebSocketMessageType.Binary, cancellationToken);
                        await AnswerPromptAsync(bytes, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // The connection dropped, or was closed because the browser left.
            }
        }

        private async Task PumpInputAsync(CancellationToken cancellationToken)
        {
            while (socket.State == WebSocketState.Open)
            {
                var (type, data) = await socket.ReceiveAsync(cancellationToken);
                switch (type)
                {
                    case WebSocketMessageType.Close:
                        return;
                    case WebSocketMessageType.Binary:
                        await WriteAsync(TelnetProtocol.Encode(data), cancellationToken);
                        break;
                    case WebSocketMessageType.Text when SessionSocket.ReadType(data) == "resize":
                        var size = JsonSerializer.Deserialize<ResizeMessage>(data, SessionJson.Options);
                        if (size is { Cols: > 0 and <= 1000, Rows: > 0 and <= 1000 })
                        {
                            recorder?.Resize(size.Cols, size.Rows);
                            if (_protocol.Resize(size.Cols, size.Rows) is { Length: > 0 } naws)
                            {
                                await WriteAsync(naws, cancellationToken);
                            }
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// With a credential, answers the first login and password prompts (once each, only just
        /// after connecting). Prompts are matched on the end of the text the server has sent so far.
        /// </summary>
        private async Task AnswerPromptAsync(byte[] data, CancellationToken cancellationToken)
        {
            if (_sentPassword || _since.Elapsed > AutoLoginWindow || (target.Username.Length == 0 && target.Password is null))
            {
                return;
            }

            _recent.Append(Encoding.UTF8.GetString(data));
            if (_recent.Length > 256)
            {
                _recent.Remove(0, _recent.Length - 256);
            }

            var text = _recent.ToString();
            if (!_sentUsername && target.Username.Length > 0 && UsernamePrompt().IsMatch(text))
            {
                _sentUsername = true;
                _recent.Clear();
                await WriteAsync(TelnetProtocol.Encode(Encoding.UTF8.GetBytes(target.Username + "\r")), cancellationToken);
            }
            else if (target.Password is { } password && PasswordPrompt().IsMatch(text))
            {
                _sentPassword = true;
                _recent.Clear();
                await WriteAsync(TelnetProtocol.Encode(Encoding.UTF8.GetBytes(password + "\r")), cancellationToken);
            }
        }

        public void Dispose() => _writeLock.Dispose();

        private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    private sealed record ResizeMessage(int Cols, int Rows);
}
