namespace NKeeper.Sdk.Web;

/// <summary>An HTTPS certificate to confirm before it's trusted (its SHA-256 fingerprint, subject, issuer and expiry).</summary>
public sealed record WebCertificateResponse(string Fingerprint, string Subject, string Issuer, DateTimeOffset Expires);
