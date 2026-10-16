using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Rdp;

/// <summary>The first message of an RDP session.</summary>
public sealed record RdpStartMessage : SessionStartMessage
{
    /// <summary>Domain for one-off credentials (saved credentials carry their own).</summary>
    [StringLength(256)]
    public string? Domain { get; init; }

    [Range(200, 8192)]
    public int Width { get; init; } = 1280;

    [Range(200, 8192)]
    public int Height { get; init; } = 720;

    [Range(48, 480)]
    public int Dpi { get; init; } = 96;

    /// <summary>Audio formats the browser can play (Guacamole.AudioPlayer.getSupportedTypes()).</summary>
    [MaxLength(16)]
    public string[] Audio { get; init; } = [];

    /// <summary>Image formats the browser can decode.</summary>
    [MaxLength(8)]
    public string[] Image { get; init; } = [];

    /// <summary>IANA time zone of the browser, e.g. "Europe/Berlin".</summary>
    [StringLength(64)]
    public string? Timezone { get; init; }
}

/// <summary>
/// Bridges one browser WebSocket to one RDP desktop through guacd. NKeeper verifies the
/// server's certificate (trust on first use), pins it in guacd, performs the Guacamole
/// handshake itself, and then relays instructions, letting only input, display and text
/// clipboard instructions through from the browser.
/// </summary>
internal sealed partial class RdpSession(
    SessionTargetResolver resolver,
    TrustedKeyStore trustedKeys,
    IOptions<GuacdSettings> guacd,
    ILogger<RdpSession> logger)
{
    public const string CertificateAlgorithm = "x509";
    private const int DefaultRdpPort = 3389;
    private const int MaxClientInstructionLength = 64 * 1024;
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> AllowedImageTypes = ["image/png", "image/jpeg", "image/webp"];

    public async Task RunAsync(SessionSocket socket, Guid userId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        SessionTarget? target = null;

        try
        {
            var start = await socket.ReceiveStartAsync<RdpStartMessage>(cancellationToken);
            target = await resolver.ResolveAsync(start, ServiceProtocol.Rdp, DefaultRdpPort, cancellationToken, start.Domain);
            if (target.Password is null)
            {
                throw new SessionException(SessionErrorCodes.MissingSecret, "Remote Desktop needs a password, and this credential has none.");
            }

            logger.LogInformation(
                "RDP session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                userId, target.HostId, target.Address, target.Port, target.CredentialSource);
            await socket.SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);

            var identity = await RdpCertificateProbe.ProbeAsync(target.Address, target.Port, target.Service.RdpSecurity ?? RdpSecurityMode.Auto, cancellationToken);
            await VerifyCertificateAsync(socket, target, identity, userId, cancellationToken);

            await using var connection = await GuacdConnection.OpenAsync(guacd.Value.Host, guacd.Value.Port, cancellationToken);
            var ready = await connection.HandshakeAsync(
                new RdpConnectionParameters(
                    target.Address,
                    target.Port,
                    target.Username,
                    target.Domain,
                    target.Password,
                    identity,
                    start.Width,
                    start.Height,
                    start.Dpi,
                    start.Audio.Where(IsAudioMimetype).Distinct().ToList(),
                    start.Image.Where(AllowedImageTypes.Contains).Distinct().DefaultIfEmpty("image/png").ToList(),
                    start.Timezone is { } tz && TimezonePattern().IsMatch(tz) ? tz : null),
                cancellationToken);

            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);
            await socket.SendAsync(Encoding.UTF8.GetBytes(ready.Raw), WebSocketMessageType.Text, cancellationToken);
            logger.LogInformation("RDP session connected for user {UserId} to host {HostId}", userId, target.HostId);

            await RelayAsync(socket, connection, target, cancellationToken);
        }
        catch (SessionException ex)
        {
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or the request was aborted.
        }
        finally
        {
            logger.LogInformation("RDP session ended for user {UserId} (host {HostId}) after {Duration}", userId, target?.HostId, started.Elapsed);
        }
    }

    /// <summary>Trust on first use for the server's TLS certificate, mirroring SSH host keys.</summary>
    private async Task VerifyCertificateAsync(SessionSocket socket, SessionTarget target, RdpServerIdentity identity, Guid userId, CancellationToken cancellationToken)
    {
        var known = target.KnownKeys.FirstOrDefault(k => k.Algorithm == CertificateAlgorithm);
        var prompt = new
        {
            type = "hostKey",
            kind = "certificate",
            port = target.Port,
            algorithm = "TLS certificate",
            fingerprint = identity.Fingerprint,
            subject = identity.Subject,
            expires = identity.NotAfter,
        };

        if (known is not null)
        {
            if (string.Equals(known.Fingerprint, identity.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            trustedKeys.LogChanged(target, ServiceProtocol.Rdp, known.Fingerprint, identity.Fingerprint);
            await socket.SendJsonAsync(new { prompt.type, status = "changed", prompt.kind, prompt.port, prompt.algorithm, prompt.fingerprint, trustedFingerprint = known.Fingerprint, prompt.subject, prompt.expires }, cancellationToken);
            throw new SessionException(SessionErrorCodes.HostKeyChanged, TrustedKeyStore.ChangedMessage("TLS certificate", target));
        }

        var trusted = await socket.AskToTrustAsync(
            new { prompt.type, status = "unknown", prompt.kind, prompt.port, prompt.algorithm, prompt.fingerprint, prompt.subject, prompt.expires },
            cancellationToken);
        if (!trusted)
        {
            throw new SessionException(SessionErrorCodes.HostKeyRejected, "The certificate was not trusted, so the connection was closed.");
        }

        await trustedKeys.TrustAsync(target, ServiceProtocol.Rdp, CertificateAlgorithm, identity.Fingerprint, userId);
    }

    private async Task RelayAsync(SessionSocket socket, GuacdConnection connection, SessionTarget target, CancellationToken cancellationToken)
    {
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = RelayFromGuacdAsync(socket, connection, target, cancellationToken);
        var input = RelayFromBrowserAsync(socket, connection, target, inputCancellation.Token);

        if (await Task.WhenAny(output, input) == output)
        {
            // guacd ended the connection (logoff, error or disconnect): tell the browser it was
            // a clean end, then complete the close handshake.
            await socket.SendAsync(Encoding.UTF8.GetBytes(GuacamoleInstruction.Encode(string.Empty, "closed")), WebSocketMessageType.Text, cancellationToken);
            await socket.BeginCloseAsync("Session ended", cancellationToken);
            inputCancellation.CancelAfter(CloseHandshakeTimeout);
        }
        else
        {
            await connection.DisposeAsync();
            await socket.AcknowledgeCloseAsync();
        }

        await Task.WhenAll(output, input).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task RelayFromGuacdAsync(SessionSocket socket, GuacdConnection connection, SessionTarget target, CancellationToken cancellationToken)
    {
        var batch = new StringBuilder();
        try
        {
            while (await connection.ReadInstructionAsync(cancellationToken) is { } first)
            {
                batch.Clear();
                foreach (var instruction in connection.DrainBuffered().Prepend(first))
                {
                    if (instruction.Opcode == "error")
                    {
                        logger.LogInformation(
                            "RDP session to host {HostId} reported: {Message} (status {Status})",
                            target.HostId, instruction.Args.ElementAtOrDefault(0), instruction.Args.ElementAtOrDefault(1));
                    }

                    batch.Append(instruction.Raw);
                }

                await socket.SendAsync(Encoding.UTF8.GetBytes(batch.ToString()), WebSocketMessageType.Text, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or FormatException)
        {
            // The browser side closed first (connection disposed) or guacd sent garbage.
        }
    }

    private async Task RelayFromBrowserAsync(SessionSocket socket, GuacdConnection connection, SessionTarget target, CancellationToken cancellationToken)
    {
        var filter = new RdpInstructionFilter();
        while (socket.State == WebSocketState.Open)
        {
            var (type, data) = await socket.ReceiveAsync(cancellationToken);
            if (type == WebSocketMessageType.Close)
            {
                return;
            }

            if (type != WebSocketMessageType.Text)
            {
                continue;
            }

            var parser = new GuacamoleParser(MaxClientInstructionLength);
            var forward = new StringBuilder();
            try
            {
                parser.Append(Encoding.UTF8.GetString(data));
                while (parser.TryRead(out var instruction))
                {
                    if (filter.IsAllowed(instruction))
                    {
                        forward.Append(instruction.Raw);
                    }
                    else
                    {
                        logger.LogWarning("Dropped RDP instruction {Opcode} from the browser for host {HostId}", instruction.Opcode, target.HostId);
                    }
                }
            }
            catch (FormatException)
            {
                logger.LogWarning("Dropped a malformed message from the browser for host {HostId}", target.HostId);
                continue;
            }

            if (forward.Length > 0)
            {
                await connection.WriteAsync(forward.ToString(), cancellationToken);
            }
        }
    }

    private static bool IsAudioMimetype(string mimetype) => AudioMimetypePattern().IsMatch(mimetype);

    [GeneratedRegex(@"^audio/L(8|16)(;rate=\d{4,6}(,channels=[12])?)?$")]
    private static partial Regex AudioMimetypePattern();

    [GeneratedRegex(@"^[A-Za-z_]+(/[A-Za-z0-9_+\-]+){0,3}$")]
    private static partial Regex TimezonePattern();
}
