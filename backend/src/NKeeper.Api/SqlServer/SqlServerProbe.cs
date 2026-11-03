using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Web;

namespace NKeeper.Api.SqlServer;

/// <summary>A SQL Server's TLS certificate, as its PRELOGIN handshake presents it.</summary>
/// <param name="Certificate">The certificate's details and fingerprint, as for HTTPS targets.</param>
/// <param name="Raw">The certificate itself (DER), to pin the real connection to.</param>
/// <param name="DnsName">The name it was issued to, which the driver checks the server against.</param>
internal sealed record SqlServerCertificate(WebCertificate Certificate, byte[] Raw, string DnsName);

/// <summary>
/// Reads a SQL Server's TLS certificate without logging in. In TDS 7.x the TLS handshake travels
/// inside PRELOGIN packets, after a plain PRELOGIN exchange that agrees on encryption: this does that
/// exchange, then the handshake, and stops there (nothing is sent over the encrypted channel).
/// </summary>
internal static class SqlServerProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const byte PreLoginPacket = 0x12;
    private const byte EndOfMessage = 0x01;
    private const byte OptionVersion = 0x00;
    private const byte OptionEncryption = 0x01;
    private const byte OptionInstance = 0x02;
    private const byte OptionThreadId = 0x03;
    private const byte OptionMars = 0x04;
    private const byte OptionTerminator = 0xFF;
    private const byte EncryptOn = 0x01;
    private const byte EncryptNotSupported = 0x02;

    /// <param name="host">The name the certificate should be valid for (the host's address).</param>
    /// <param name="dialAddress">Where to connect instead (a jump-host tunnel).</param>
    /// <exception cref="SessionException">The server can't be reached, isn't SQL Server, or doesn't offer encryption.</exception>
    public static async Task<SqlServerCertificate> ProbeAsync(string host, int port, CancellationToken cancellationToken, string? dialAddress = null, int? dialPort = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(dialAddress ?? host, dialPort ?? port, timeout.Token);
            var network = client.GetStream();

            await network.WriteAsync(PreLoginRequest(), timeout.Token);
            var encryption = ReadEncryption(await ReadMessageAsync(network, timeout.Token));
            if (encryption == EncryptNotSupported)
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed,
                    $"{host}:{port} doesn't offer encryption, so NKeeper won't send it a login. Give the SQL Server a certificate (or let it use its self-signed one).");
            }

            var errors = SslPolicyErrors.None;
            await using var tls = new SslStream(new PreLoginTlsStream(network), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    // Record the verdict instead of failing: an invalid certificate leads to a trust prompt.
#pragma warning disable CA5359 // Nothing is sent over this connection; it only reads the certificate.
                    RemoteCertificateValidationCallback = (_, _, _, policyErrors) =>
                    {
                        errors = policyErrors;
                        return true;
                    },
#pragma warning restore CA5359
                },
                timeout.Token);

            using var certificate = new X509Certificate2(tls.RemoteCertificate!);
            var raw = certificate.RawData;
            return new SqlServerCertificate(
                new WebCertificate(errors == SslPolicyErrors.None, WebTargetProbe.Fingerprint(raw), certificate.Subject, certificate.Issuer, certificate.NotAfter.ToUniversalTime()),
                raw,
                certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false) is { Length: > 0 } dns ? dns : host);
        }
        catch (SocketException ex)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {host}:{port} ({SocketErrors.Describe(ex.SocketErrorCode, "SQL Server")}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"Timed out connecting to {host}:{port}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Authentication.AuthenticationException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{host}:{port} didn't answer like a SQL Server. Is the port right?");
        }
    }

    /// <summary>PRELOGIN asking for encryption, with the options SqlClient sends (VERSION, ENCRYPTION, INSTOPT, THREADID, MARS).</summary>
    private static byte[] PreLoginRequest()
    {
        const int table = (5 * 5) + 1;
        byte[] payload =
        [
            OptionVersion, 0, table, 0, 6,
            OptionEncryption, 0, table + 6, 0, 1,
            OptionInstance, 0, table + 7, 0, 1,
            OptionThreadId, 0, table + 8, 0, 4,
            OptionMars, 0, table + 12, 0, 1,
            OptionTerminator,
            // Version 0.0.0.0, sub-build 0: servers don't check the client's.
            0, 0, 0, 0, 0, 0,
            EncryptOn,
            // Any instance (an empty name), thread 0, no MARS.
            0,
            0, 0, 0, 0,
            0,
        ];
        var packet = new byte[8 + payload.Length];
        WriteHeader(packet, PreLoginPacket, payload.Length, packetId: 1);
        payload.CopyTo(packet, 8);
        return packet;
    }

    private static void WriteHeader(Span<byte> header, byte type, int payloadLength, byte packetId)
    {
        header[0] = type;
        header[1] = EndOfMessage;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(payloadLength + 8));
        header[4] = 0;
        header[5] = 0;
        header[6] = packetId;
        header[7] = 0;
    }

    /// <summary>The server's PRELOGIN answer, all its packets joined.</summary>
    private static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var header = new byte[8];
        while (true)
        {
            await stream.ReadExactlyAsync(header, cancellationToken);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            if (length < 8 || message.Length + length > 64 * 1024)
            {
                throw new InvalidDataException("Not a TDS packet.");
            }

            var payload = new byte[length - 8];
            await stream.ReadExactlyAsync(payload, cancellationToken);
            message.Write(payload);
            if ((header[1] & EndOfMessage) != 0)
            {
                return message.ToArray();
            }
        }
    }

    /// <summary>The ENCRYPTION option of a PRELOGIN answer.</summary>
    internal static byte ReadEncryption(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i + 5 <= payload.Length && payload[i] != OptionTerminator; i += 5)
        {
            if (payload[i] != OptionEncryption)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadUInt16BigEndian(payload[(i + 1)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[(i + 3)..]);
            if (length < 1 || offset >= payload.Length)
            {
                break;
            }

            return payload[offset];
        }

        throw new InvalidDataException("The PRELOGIN answer has no ENCRYPTION option.");
    }

    /// <summary>
    /// Carries TLS records inside PRELOGIN packets, as TDS 7.x does during the handshake: each write
    /// becomes a packet, and reads return the packets' payloads.
    /// </summary>
    private sealed class PreLoginTlsStream(Stream inner) : Stream
    {
        private const int MaxPayload = 4096 - 8;
        private readonly byte[] _header = new byte[8];
        private int _remaining;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_remaining == 0)
            {
                await inner.ReadExactlyAsync(_header, cancellationToken);
                var length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
                if (length < 8)
                {
                    throw new InvalidDataException("Not a TDS packet.");
                }

                _remaining = length - 8;
            }

            // SslStream makes zero-byte reads to wait for data: there is some now.
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            _remaining -= read;
            return read;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                var chunk = buffer[..Math.Min(buffer.Length, MaxPayload)];
                var packet = new byte[8 + chunk.Length];
                // Like SqlClient: packet ID 0 throughout the handshake.
                WriteHeader(packet, PreLoginPacket, chunk.Length, packetId: 0);
                chunk.CopyTo(packet.AsMemory(8));
                await inner.WriteAsync(packet, cancellationToken);
                buffer = buffer[chunk.Length..];
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
