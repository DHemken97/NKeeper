using NKeeper.Sdk.Sessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Sdk.Web;

/// <summary>An HTTPS target presented a different certificate from the one trusted for it.</summary>
public sealed class CertificateChangedException(string message, string fingerprint, string trustedFingerprint) : Exception(message)
{
    public string Fingerprint { get; } = fingerprint;

    public string TrustedFingerprint { get; } = trustedFingerprint;
}

/// <summary>
/// The outcome of checking an HTTPS target's certificate: either connect (pinning
/// <see cref="PinnedFingerprint"/> when the certificate isn't publicly valid), or ask the user to
/// trust <see cref="Prompt"/> first.
/// </summary>
public sealed record CertificateTrustResult(string? PinnedFingerprint, WebCertificate? Prompt);

/// <summary>
/// Trust on first use for HTTPS targets (web sessions, iDRACs): a publicly valid certificate is
/// accepted; otherwise the user confirms it once, and a later different certificate is refused.
/// </summary>
public static class CertificateTrust
{
    public const string Algorithm = "x509";

    /// <param name="keyProtocol">The protocol the certificate is trusted under (Https, or Idrac for an iDRAC's own address).</param>
    /// <param name="trustedKeys">The host's trusted keys.</param>
    /// <param name="trustFingerprint">The fingerprint the user chose to trust after a prompt.</param>
    /// <exception cref="CertificateChangedException">The certificate differs from the trusted one.</exception>
    /// <exception cref="SessionException">The target couldn't be reached over TLS.</exception>
    public static async Task<CertificateTrustResult> CheckAsync(
        Guid hostId,
        string address,
        int port,
        ServiceProtocol keyProtocol,
        IEnumerable<HostTrustedKey> trustedKeys,
        string? trustFingerprint,
        Guid userId,
        TrustedKeyStore store,
        CancellationToken cancellationToken,
        JumpTunnel? via = null)
    {
        var certificate = await WebTargetProbe.ProbeAsync(address, port, cancellationToken, via?.Address, via?.Port);
        if (certificate.IsValid)
        {
            return new CertificateTrustResult(null, null);
        }

        var known = trustedKeys.FirstOrDefault(k => k.Protocol == keyProtocol && k.Port == port && k.Algorithm == Algorithm);
        if (known is not null)
        {
            if (!string.Equals(known.Fingerprint, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                store.LogChanged(hostId, port, keyProtocol, known.Fingerprint, certificate.Fingerprint);
                throw new CertificateChangedException(
                    TrustedKeyStore.ChangedMessage("TLS certificate", address, port), certificate.Fingerprint, known.Fingerprint);
            }

            return new CertificateTrustResult(certificate.Fingerprint, null);
        }

        if (!string.Equals(trustFingerprint, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return new CertificateTrustResult(null, certificate);
        }

        await store.TrustAsync(hostId, port, keyProtocol, Algorithm, certificate.Fingerprint, userId);
        return new CertificateTrustResult(certificate.Fingerprint, null);
    }
}
