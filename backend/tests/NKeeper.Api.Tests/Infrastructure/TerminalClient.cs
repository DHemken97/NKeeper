using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// Drives a remote session WebSocket the way the browser does: JSON control messages, then
/// terminal bytes (SSH) or Guacamole instructions (RDP, answering guacd's syncs).
/// </summary>
public sealed class TerminalClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly bool _guacamole;
    private readonly StringBuilder _output = new();
    private readonly Queue<JsonElement> _messages = new();
    private bool _connected;

    private TerminalClient(WebSocket socket, bool guacamole)
    {
        _socket = socket;
        _guacamole = guacamole;
    }

    /// <summary>RDP: opcodes of the Guacamole instructions received after "connected", in order.</summary>
    public List<string> Instructions { get; } = [];

    /// <summary>RDP: the Guacamole "error" instruction's message, if any.</summary>
    public string? GuacamoleError { get; private set; }

    /// <summary>All terminal output received so far.</summary>
    public string Output => _output.ToString();

    public bool Closed { get; private set; }

    public static async Task<TerminalClient> ConnectAsync(
        NKeeperApiFactory factory, CookieContainer cookies, CancellationToken cancellationToken, string origin = "http://localhost", string protocol = "ssh")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookies.GetCookieHeader(new Uri("http://localhost/"));
            request.Headers.Origin = origin;
        };
        return new TerminalClient(await client.ConnectAsync(new Uri($"ws://localhost/api/{protocol}/session"), cancellationToken), protocol == "rdp");
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

    /// <summary>RDP: waits until a Guacamole instruction with this opcode arrives.</summary>
    public async Task WaitForInstructionAsync(string opcode, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        while (!Instructions.Contains(opcode))
        {
            await ReceiveOneAsync(timeout.Token);
        }
    }

    public Task SendInstructionAsync(string instruction, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(instruction), WebSocketMessageType.Text, true, cancellationToken);

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
            case WebSocketMessageType.Text when _guacamole && _connected:
                await HandleGuacamoleAsync(Encoding.UTF8.GetString(message.ToArray()), cancellationToken);
                break;
            default:
                var json = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
                _connected |= json.GetProperty("type").GetString() == "connected";
                _messages.Enqueue(json);
                break;
        }
    }

    /// <summary>Records each instruction and acknowledges syncs, as guacd waits for them before sending more.</summary>
    private async Task HandleGuacamoleAsync(string text, CancellationToken cancellationToken)
    {
        var position = 0;
        while (position < text.Length)
        {
            var elements = new List<string>();
            while (true)
            {
                var dot = text.IndexOf('.', position);
                var length = int.Parse(text.AsSpan(position, dot - position), System.Globalization.CultureInfo.InvariantCulture);
                // Test data is ASCII apart from rare clipboard text, so UTF-16 length is adequate here.
                elements.Add(text.Substring(dot + 1, length));
                position = dot + 1 + length;
                if (text[position++] == ';')
                {
                    break;
                }
            }

            Instructions.Add(elements[0]);
            if (elements[0] == "sync")
            {
                await SendInstructionAsync($"4.sync,{elements[1].Length}.{elements[1]};", cancellationToken);
            }
            else if (elements[0] == "error")
            {
                GuacamoleError = elements[1];
            }
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
