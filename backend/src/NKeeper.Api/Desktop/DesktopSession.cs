using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NKeeper.Sdk.Access;
using NKeeper.Api.Configuration;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Desktop;

/// <summary>The first message of a remote desktop (RDP or VNC) session.</summary>
public sealed record DesktopStartMessage : SessionStartMessage
{
    /// <summary>RDP: domain for one-off credentials (saved credentials carry their own).</summary>
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
/// Bridges one browser WebSocket to one remote desktop (RDP or VNC) through guacd. NKeeper performs
/// the Guacamole handshake itself, then relays instructions, letting only input, display and text
/// clipboard instructions through from the browser. For RDP, the server's certificate is verified
/// (trust on first use) and pinned in guacd. VNC has no certificate to verify.
/// </summary>
internal sealed partial class DesktopSession(
    SessionTargetResolver resolver,
    TrustedKeyStore trustedKeys,
    IOptions<GuacdSettings> guacd,
    NKeeper.Sdk.Auditing.AuditLog audit,
    ILogger<DesktopSession> logger)
{
    public const string CertificateAlgorithm = "x509";
    private const int MaxClientInstructionLength = 64 * 1024;
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> AllowedImageTypes = ["image/png", "image/jpeg", "image/webp"];

    public async Task RunAsync(SessionSocket socket, RequestUser user, ServiceProtocol protocol, CancellationToken cancellationToken)
    {
        var name = protocol == ServiceProtocol.Rdp ? "RDP" : "VNC";
        var started = Stopwatch.StartNew();
        SessionTarget? target = null;
        NKeeper.Sdk.Auditing.AuditedSession? audited = null;

        try
        {
            var start = await socket.ReceiveStartAsync<DesktopStartMessage>(cancellationToken);
            audited = audit.BeginSession(name, start.HostId, user);
            target = protocol == ServiceProtocol.Rdp
                ? await resolver.ResolveAsync(start, user, protocol, 3389, cancellationToken, start.Domain)
                // VNC servers usually ask for a password only.
                : await resolver.ResolveAsync(start, user, protocol, 5900, cancellationToken, requireUsername: false);
            SessionChecks.RefuseJumpHost(target, name);
            if (protocol == ServiceProtocol.Rdp && target.Password is null)
            {
                throw new SessionException(SessionErrorCodes.MissingSecret, "Remote Desktop needs a password, and this credential has none.");
            }

            logger.LogInformation(
                "{Protocol} session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                name, user.Id, target.HostId, target.Address, target.Port, target.CredentialSource);
            await socket.SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);

            var values = protocol == ServiceProtocol.Rdp
                ? RdpValues(target, await VerifyCertificateAsync(socket, target, user.Id, cancellationToken), start)
                : VncValues(target);

            await using var connection = await GuacdConnection.OpenAsync(guacd.Value.Host, guacd.Value.Port, cancellationToken);
            var client = new GuacdClientInfo(
                start.Width,
                start.Height,
                start.Dpi,
                start.Audio.Where(IsAudioMimetype).Distinct().ToList(),
                start.Image.Where(AllowedImageTypes.Contains).Distinct().DefaultIfEmpty("image/png").ToList(),
                start.Timezone is { } tz && TimezonePattern().IsMatch(tz) ? tz : null);
            var ready = await connection.HandshakeAsync(protocol == ServiceProtocol.Rdp ? "rdp" : "vnc", client, values, cancellationToken);

            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);
            await socket.SendAsync(Encoding.UTF8.GetBytes(ready.Raw), WebSocketMessageType.Text, cancellationToken);
            logger.LogInformation("{Protocol} session connected for user {UserId} to host {HostId}", name, user.Id, target.HostId);
            await audited.ConnectedAsync(target.Display);

            await RelayAsync(socket, connection, target, name, cancellationToken);
        }
        catch (SessionException ex)
        {
            await (audited?.FailedAsync(ex.Message, target?.Display) ?? Task.CompletedTask);
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away, the request was aborted, or an administrator ended the session.
        }
        finally
        {
            await (audited?.EndAsync() ?? Task.CompletedTask);
            logger.LogInformation("{Protocol} session ended for user {UserId} (host {HostId}) after {Duration}", name, user.Id, target?.HostId, started.Elapsed);
        }
    }

    /// <summary>
    /// guacd's RDP parameters. Everything not listed stays off: no drive, printer, SFTP,
    /// microphone, recording, gateway or Wake-on-LAN, and certificates are never ignored.
    /// </summary>
    private static Dictionary<string, string> RdpValues(SessionTarget target, RdpServerIdentity identity, DesktopStartMessage start) => new()
    {
        ["hostname"] = target.Address,
        ["port"] = GuacdConnection.Invariant(target.Port),
        ["username"] = target.Username,
        ["domain"] = target.Domain ?? string.Empty,
        ["password"] = target.Password!,
        ["security"] = identity.GuacdSecurity,
        // Pin the certificate the user verified; FreeRDP refuses any other.
        ["cert-fingerprints"] = identity.PinnedFingerprint,
        ["width"] = GuacdConnection.Invariant(start.Width),
        ["height"] = GuacdConnection.Invariant(start.Height),
        ["dpi"] = GuacdConnection.Invariant(start.Dpi),
        ["resize-method"] = "display-update",
        ["client-name"] = "NKeeper",
        ["enable-font-smoothing"] = "true",
        ["disable-download"] = "true",
        ["disable-upload"] = "true",
        ["timezone"] = start.Timezone is { } tz && TimezonePattern().IsMatch(tz) ? tz : string.Empty,
    };

    /// <summary>
    /// guacd's VNC parameters. Everything not listed stays off: no SFTP, audio (PulseAudio),
    /// repeater, reverse connection, recording or Wake-on-LAN.
    /// </summary>
    private static Dictionary<string, string> VncValues(SessionTarget target) => new()
    {
        ["hostname"] = target.Address,
        ["port"] = GuacdConnection.Invariant(target.Port),
        ["username"] = target.Username,
        ["password"] = target.Password ?? string.Empty,
        // The pointer is drawn by the browser: smoother than waiting for the server to repaint it.
        ["cursor"] = "local",
        ["color-depth"] = "24",
    };

    /// <summary>Trust on first use for the RDP server's TLS certificate, mirroring SSH host keys.</summary>
    private async Task<RdpServerIdentity> VerifyCertificateAsync(SessionSocket socket, SessionTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var identity = await RdpCertificateProbe.ProbeAsync(target.Address, target.Port, target.Service.RdpSecurity ?? RdpSecurityMode.Auto, cancellationToken);
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
                return identity;
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
        return identity;
    }

    private async Task RelayAsync(SessionSocket socket, GuacdConnection connection, SessionTarget target, string name, CancellationToken cancellationToken)
    {
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = RelayFromGuacdAsync(socket, connection, target, name, cancellationToken);
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

    private async Task RelayFromGuacdAsync(SessionSocket socket, GuacdConnection connection, SessionTarget target, string name, CancellationToken cancellationToken)
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
                            "{Protocol} session to host {HostId} reported: {Message} (status {Status})",
                            name, target.HostId, instruction.Args.ElementAtOrDefault(0), instruction.Args.ElementAtOrDefault(1));
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
        var filter = new GuacamoleInstructionFilter();
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
                        logger.LogWarning("Dropped instruction {Opcode} from the browser for host {HostId}", instruction.Opcode, target.HostId);
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
