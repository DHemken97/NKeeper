using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Configuration;

public sealed class CorsSettings
{
    public const string SectionName = "Cors";
    public const string PolicyName = "Frontend";

    /// <summary>
    /// Exact browser origins allowed to call the API with credentials (e.g. http://localhost:5173).
    /// Leave empty when the frontend is served from the same origin as the API (the default setup).
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    internal bool IsValid() =>
        AllowedOrigins.All(o => Uri.TryCreate(o, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath == "/"
            && !o.EndsWith('/'));
}

public sealed class LoginRateLimitSettings
{
    public const string SectionName = "RateLimiting:Login";
    public const string PolicyName = "login";

    /// <summary>Login attempts allowed per client IP address within <see cref="Window"/>.</summary>
    [Range(1, 10_000)]
    public int PermitLimit { get; set; } = 10;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Honour X-Forwarded-For/-Proto from a reverse proxy. Only enable when the API is reachable
    /// exclusively through that proxy, otherwise clients can spoof their IP address.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>CIDR ranges of trusted proxies. Empty means any proxy is trusted (see <see cref="Enabled"/>).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Number of proxy hops to unwind. 1 for the bundled nginx; 2 when another TLS-terminating
    /// proxy sits in front of it, so rate limiting sees the real client address.
    /// </summary>
    [Range(1, 5)]
    public int ForwardLimit { get; set; } = 1;

    internal bool IsValid() => KnownNetworks.All(n => System.Net.IPNetwork.TryParse(n, out _));
}

/// <summary>Where Apache Guacamole's guacd daemon (the RDP gateway) listens. Never expose it publicly.</summary>
public sealed class GuacdSettings
{
    public const string SectionName = "Guacd";

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 4822;
}
