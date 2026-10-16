using System.Globalization;
using System.Net.Sockets;
using System.Text;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Rdp;

/// <summary>Everything guacd needs to open one RDP connection.</summary>
internal sealed record RdpConnectionParameters(
    string Hostname,
    int Port,
    string Username,
    string? Domain,
    string Password,
    RdpServerIdentity Identity,
    int Width,
    int Height,
    int Dpi,
    IReadOnlyList<string> AudioMimetypes,
    IReadOnlyList<string> ImageMimetypes,
    string? Timezone);

/// <summary>
/// A connection to guacd. NKeeper performs the Guacamole handshake itself, so the browser can
/// never choose the protocol, target or options; afterwards instructions are relayed.
/// </summary>
internal sealed class GuacdConnection : IAsyncDisposable
{
    private const int MaxInstructionLength = 8 * 1024 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly GuacamoleParser _parser = new(MaxInstructionLength);
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private readonly char[] _charBuffer = new char[64 * 1024];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private GuacdConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<GuacdConnection> OpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return new GuacdConnection(client);
        }
        catch (SocketException)
        {
            client.Dispose();
            throw new SessionException(
                SessionErrorCodes.GatewayUnavailable,
                "NKeeper's Remote Desktop gateway (guacd) isn't reachable. Check that the guacd container is running.");
        }
    }

    /// <summary>
    /// Selects RDP, sends the client's capabilities and the connection parameters, and waits
    /// for "ready", which is returned so it can be relayed to the browser's Guacamole client.
    /// The RDP connection itself completes afterwards; failures arrive as "error" instructions.
    /// </summary>
    public async Task<GuacamoleInstruction> HandshakeAsync(RdpConnectionParameters parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        await WriteAsync(GuacamoleInstruction.Encode("select", "rdp"), timeout.Token);
        var args = await ReadInstructionAsync(timeout.Token);
        if (args is not { Opcode: "args" })
        {
            throw new SessionException(SessionErrorCodes.GatewayUnavailable, "The Remote Desktop gateway (guacd) didn't respond as expected.");
        }

        var dpi = parameters.Dpi.ToString(CultureInfo.InvariantCulture);
        var handshake = new StringBuilder()
            .Append(GuacamoleInstruction.Encode("size", Invariant(parameters.Width), Invariant(parameters.Height), dpi))
            .Append(GuacamoleInstruction.Encode("audio", parameters.AudioMimetypes))
            .Append(GuacamoleInstruction.Encode("video"))
            .Append(GuacamoleInstruction.Encode("image", parameters.ImageMimetypes));
        if (parameters.Timezone is not null)
        {
            handshake.Append(GuacamoleInstruction.Encode("timezone", parameters.Timezone));
        }

        var values = ConnectionValues(parameters);
        handshake.Append(GuacamoleInstruction.Encode("connect", args.Args.Select(name => name.StartsWith("VERSION_", StringComparison.Ordinal)
            ? name
            : values.GetValueOrDefault(name, string.Empty))));
        await WriteAsync(handshake.ToString(), timeout.Token);

        var reply = await ReadInstructionAsync(timeout.Token);
        if (reply is not { Opcode: "ready" })
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                reply is { Opcode: "error", Args.Count: > 0 } ? $"The connection failed: {reply.Args[0]}" : "The Remote Desktop gateway (guacd) refused the connection.");
        }

        return reply;
    }

    /// <summary>
    /// guacd's RDP parameters. Everything not listed is left empty (off): no drive, printer,
    /// SFTP, microphone, recording, gateway or Wake-on-LAN, and certificates are never ignored.
    /// </summary>
    private static Dictionary<string, string> ConnectionValues(RdpConnectionParameters p) => new()
    {
        ["hostname"] = p.Hostname,
        ["port"] = Invariant(p.Port),
        ["username"] = p.Username,
        ["domain"] = p.Domain ?? string.Empty,
        ["password"] = p.Password,
        ["security"] = p.Identity.GuacdSecurity,
        // Pin the certificate the user verified; FreeRDP refuses any other.
        ["cert-fingerprints"] = p.Identity.PinnedFingerprint,
        ["width"] = Invariant(p.Width),
        ["height"] = Invariant(p.Height),
        ["dpi"] = Invariant(p.Dpi),
        ["resize-method"] = "display-update",
        ["client-name"] = "NKeeper",
        ["enable-font-smoothing"] = "true",
        ["disable-download"] = "true",
        ["disable-upload"] = "true",
        ["timezone"] = p.Timezone ?? string.Empty,
    };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The next instruction, or null when guacd closed the connection.</summary>
    public async Task<GuacamoleInstruction?> ReadInstructionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_parser.TryRead(out var instruction))
            {
                return instruction;
            }

            var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            var chars = _decoder.GetChars(_readBuffer, 0, read, _charBuffer, 0);
            _parser.Append(_charBuffer.AsSpan(0, chars));
        }
    }

    /// <summary>All instructions already received after the next one, so they can be sent together.</summary>
    public IEnumerable<GuacamoleInstruction> DrainBuffered()
    {
        while (_parser.BufferedLength > 0 && _parser.TryRead(out var instruction))
        {
            yield return instruction;
        }
    }

    public async Task WriteAsync(string instructions, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(instructions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
        _writeLock.Dispose();
    }
}
