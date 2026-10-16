using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Text.Json;

namespace NKeeper.Api.RemoteSessions;

/// <summary>
/// The browser side of a remote session: framing, size limits, serialized sends, the JSON
/// control messages shared by all protocols, and a clean close handshake.
/// </summary>
internal sealed class SessionSocket(WebSocket socket) : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrustDecisionTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketState State => socket.State;

    public void Dispose() => _sendLock.Dispose();

    /// <summary>Receives one whole message (reassembling fragments), or Close.</summary>
    public async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "Message too large.");
            }

            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }

    /// <summary>Receives and validates the first message of a session.</summary>
    public async Task<T> ReceiveStartAsync<T>(CancellationToken cancellationToken) where T : SessionStartMessage
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);

        var (type, data) = await ReceiveAsync(timeout.Token);
        if (type != WebSocketMessageType.Text || ReadType(data) != "start")
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "The session must begin with a start message.");
        }

        T? start;
        try
        {
            start = JsonSerializer.Deserialize<T>(data, SessionJson.Options);
        }
        catch (JsonException)
        {
            start = null;
        }

        if (start is null || !Validator.TryValidateObject(start, new ValidationContext(start), null, validateAllProperties: true))
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "The start message is invalid.");
        }

        return start;
    }

    /// <summary>
    /// Shows the user an unknown server identity and waits for their decision. Anything other
    /// than an explicit "trust" (including a timeout or a closed tab) is a rejection.
    /// </summary>
    public async Task<bool> AskToTrustAsync(object prompt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TrustDecisionTimeout);
        try
        {
            await SendJsonAsync(prompt, timeout.Token);
            while (true)
            {
                var (type, data) = await ReceiveAsync(timeout.Token);
                if (type == WebSocketMessageType.Close)
                {
                    return false;
                }

                if (type == WebSocketMessageType.Text && ReadType(data) == "hostKeyDecision")
                {
                    return JsonSerializer.Deserialize<TrustDecision>(data, SessionJson.Options)?.Trust == true;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or SessionException)
        {
            return false;
        }
    }

    public Task SendJsonAsync(object message, CancellationToken cancellationToken) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, SessionJson.Options), WebSocketMessageType.Text, cancellationToken);

    public async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(data, type, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Tells the client why the session failed, then closes the socket cleanly.</summary>
    public async Task SendErrorAndCloseAsync(string code, string message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await SendJsonAsync(new { type = "error", code, message }, cancellationToken);
            await CloseAsync(code, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or SessionException)
        {
            // The browser is already gone.
        }
    }

    /// <summary>
    /// Starts the close handshake and waits (briefly) for the browser to acknowledge it:
    /// returning earlier can drop the messages just sent.
    /// </summary>
    public async Task CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CloseHandshakeTimeout);
        while (socket.State == WebSocketState.CloseSent)
        {
            var (type, _) = await ReceiveAsync(timeout.Token);
            if (type == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    /// <summary>Sends our close frame only; whoever is receiving gets the browser's acknowledgement.</summary>
    public async Task BeginCloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
        }
    }

    /// <summary>Acknowledges a close the browser started, completing the handshake.</summary>
    public async Task AcknowledgeCloseAsync()
    {
        if (socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session closed", CancellationToken.None);
        }
    }

    public static string? ReadType(byte[] json)
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

    private sealed record TrustDecision(bool Trust);
}
