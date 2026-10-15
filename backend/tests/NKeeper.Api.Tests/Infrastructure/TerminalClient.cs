using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>Drives the terminal WebSocket protocol the way the browser does.</summary>
public sealed class TerminalClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly StringBuilder _output = new();
    private readonly Queue<JsonElement> _messages = new();

    private TerminalClient(WebSocket socket) => _socket = socket;

    /// <summary>All terminal output received so far.</summary>
    public string Output => _output.ToString();

    public bool Closed { get; private set; }

    public static async Task<TerminalClient> ConnectAsync(
        NKeeperApiFactory factory, CookieContainer cookies, CancellationToken cancellationToken, string origin = "http://localhost")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookies.GetCookieHeader(new Uri("http://localhost/"));
            request.Headers.Origin = origin;
        };
        return new TerminalClient(await client.ConnectAsync(new Uri("ws://localhost/api/ssh/session"), cancellationToken));
    }

    public Task SendJsonAsync(object message, CancellationToken cancellationToken) =>
        _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, cancellationToken);

    public Task SendInputAsync(string text, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, cancellationToken);

    /// <summary>The next control message, collecting terminal output that arrives before it.</summary>
    public async Task<JsonElement> NextMessageAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        while (_messages.Count == 0)
        {
            await ReceiveOneAsync(timeout.Token);
        }

        return _messages.Dequeue();
    }

    /// <summary>Waits for the next control message and checks its type.</summary>
    public async Task<JsonElement> ExpectAsync(string type, CancellationToken cancellationToken)
    {
        var message = await NextMessageAsync(cancellationToken);
        Assert.Equal(type, message.GetProperty("type").GetString());
        return message;
    }

    public async Task WaitForOutputAsync(string text, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        while (!Output.Contains(text, StringComparison.Ordinal))
        {
            await ReceiveOneAsync(timeout.Token);
        }
    }

    private async Task ReceiveOneAsync(CancellationToken cancellationToken)
    {
        if (Closed)
        {
            throw new InvalidOperationException($"The socket closed. Output so far:\n{Output}");
        }

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        switch (result.MessageType)
        {
            case WebSocketMessageType.Close:
                Closed = true;
                break;
            case WebSocketMessageType.Binary:
                _output.Append(Encoding.UTF8.GetString(message.ToArray()));
                break;
            default:
                _messages.Enqueue(JsonDocument.Parse(message.ToArray()).RootElement.Clone());
                break;
        }
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return timeout;
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        _socket.Dispose();
    }
}

public static class SignedInCookies
{
    /// <summary>Signs in as the admin and returns the cookies, for handing to a WebSocket handshake.</summary>
    public static async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync(NKeeperApiFactory factory, CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        (await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, cancellationToken)).EnsureSuccessStatusCode();
        return (client, cookies);
    }
}
