using NKeeper.Sdk.Sessions;

namespace NKeeper.Sdk.Web;

/// <summary>
/// Either a web session is ready (the browser opens <see cref="LaunchPath"/> on the session's own
/// origin, <c>nkw-&lt;SessionId&gt;.&lt;BaseDomain or web.&lt;NKeeper host&gt;&gt;</c>), or the target's
/// certificate needs to be trusted first. The web app's web session view shows either.
/// </summary>
public sealed record OpenWebSessionResponse(
    string Status, string? SessionId, string? LaunchPath, string? BaseDomain, string? Target, WebCertificateResponse? Certificate)
{
    /// <summary>The target's certificate must be trusted first.</summary>
    public static OpenWebSessionResponse TrustRequired(Uri target, WebCertificate certificate) =>
        new("trustRequired", null, null, null, target.ToString(), new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
}

/// <summary>
/// Web sessions: NKeeper connects to a web interface itself and relays it to the browser on an
/// isolated origin, so the browser never contacts the target. A plugin opens one to show a device's
/// own page (an iDRAC's virtual console) in a tab.
/// </summary>
public interface IWebSessions
{
    /// <summary>
    /// Opens a web session for the signed-in user to <paramref name="target"/>, whose launch link
    /// lands on <paramref name="startPath"/>, and records <c>session.start</c> in the audit trail.
    /// </summary>
    /// <param name="pinnedFingerprint">The HTTPS certificate to accept, when it isn't publicly valid (see <c>CertificateTrust</c>).</param>
    /// <param name="tunnel">A jump-host tunnel to the target, which the session owns from now on (also when it can't be opened).</param>
    /// <param name="auditDetail">What it is, for the audit trail, e.g. "iDRAC console · 10.0.0.5:443".</param>
    /// <exception cref="SessionException">With <see cref="SessionErrorCodes.TooManySessions"/> when the user has too many open.</exception>
    Task<OpenWebSessionResponse> OpenAsync(
        HttpContext context, Guid hostId, Uri target, string? pinnedFingerprint, string startPath, JumpTunnel? tunnel, string auditDetail);

    /// <summary>A target's URL, e.g. https://10.0.0.5:8443/ (IPv6 addresses bracketed).</summary>
    static Uri TargetUri(bool https, string address, int port) =>
        new UriBuilder(https ? "https" : "http", address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address, port).Uri;
}
