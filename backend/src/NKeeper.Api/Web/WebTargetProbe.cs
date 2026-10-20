using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Web;

/// <summary>An HTTPS server's certificate, and whether it is publicly valid for the host name.</summary>
public sealed record WebCertificate(bool IsValid, string Fingerprint, string Subject, string Issuer, DateTimeOffset NotAfter);

/// <summary>Connects to an HTTPS target and reads its certificate (nothing is sent over the connection).</summary>
public static class WebTargetProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>SHA-256 of a certificate as colon-separated upper-case hex.</summary>
    public static string Fingerprint(byte[] rawCertificate)
    {
        var hex = Convert.ToHexString(SHA256.HashData(rawCertificate));
        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <param name="dialAddress">Where to connect instead (a jump-host tunnel); the certificate is still checked for <paramref name="host"/>.</param>
    public static async Task<WebCertificate> ProbeAsync(string host, int port, CancellationToken cancellationToken, string? dialAddress = null, int? dialPort = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(dialAddress ?? host, dialPort ?? port, timeout.Token);
            var errors = SslPolicyErrors.None;
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
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
            return new WebCertificate(
                errors == SslPolicyErrors.None,
                Fingerprint(certificate.RawData),
                certificate.Subject,
                certificate.Issuer,
                certificate.NotAfter.ToUniversalTime());
        }
        catch (SocketException ex)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {host}:{port} ({SocketErrors.Describe(ex.SocketErrorCode, "HTTPS")}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"Timed out connecting to {host}:{port}.");
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{host}:{port} didn't complete a TLS handshake. Is it an HTTPS server?");
        }
    }
}
