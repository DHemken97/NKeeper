using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;

namespace NKeeper.Api.Web;

/// <summary>
/// One web session: a target (scheme, host, port) that one user may browse through NKeeper.
/// Requests are made by the server with this session's own HTTP client, which only accepts the
/// target's certificate if it is publicly valid or matches the fingerprint the user trusted.
/// </summary>
public sealed class WebSession : IDisposable
{
    internal WebSession(Guid userId, Guid hostId, Uri target, string? pinnedFingerprint, string? embedderOrigin, DateTimeOffset now)
    {
        EmbedderOrigin = embedderOrigin;
        Id = RandomId(16);
        Secret = Base64Url(32);
        LaunchToken = Base64Url(32);
        UserId = userId;
        HostId = hostId;
        Target = target;
        CreatedAtUtc = LastSeenUtc = LastValidatedUtc = now;
        LaunchTokenExpiresUtc = now.AddMinutes(2);

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Publicly valid certificates pass; otherwise only the exact certificate the user trusted.
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (pinnedFingerprint is not null && certificate is not null
                        && string.Equals(WebTargetProbe.Fingerprint(certificate.GetRawCertData()), pinnedFingerprint, StringComparison.OrdinalIgnoreCase)),
            },
        };
        Client = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>128 random bits as hex, used as a DNS label: <c>nkw-&lt;Id&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>Value of the browser cookie that proves the request comes from the user who launched it.</summary>
    public string Secret { get; }

    /// <summary>One-time token in the launch URL, exchanged for the cookie.</summary>
    public string? LaunchToken { get; private set; }

    public DateTimeOffset LaunchTokenExpiresUtc { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    /// <summary>e.g. https://10.0.0.5:8443/</summary>
    public Uri Target { get; }

    /// <summary>NKeeper's own origin, the only page allowed to show this session in a frame.</summary>
    public string? EmbedderOrigin { get; }

    public HttpMessageInvoker Client { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastSeenUtc { get; internal set; }

    /// <summary>When the user's account and host access were last re-checked.</summary>
    public DateTimeOffset LastValidatedUtc { get; internal set; }

    /// <summary>Consumes the launch token: valid once, and only for a short time.</summary>
    internal bool TryRedeemLaunchToken(string token, DateTimeOffset now)
    {
        var expected = LaunchToken;
        if (expected is null || now > LaunchTokenExpiresUtc
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            return false;
        }

        LaunchToken = null;
        return true;
    }

    internal bool HasSecret(string? value) =>
        value is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(value), System.Text.Encoding.ASCII.GetBytes(Secret));

    public void Dispose() => Client.Dispose();

    /// <summary>Random lower-case hex: unguessable and a valid DNS label.</summary>
    private static string RandomId(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Open web sessions, in memory (per API instance; restarting the API ends them).</summary>
public sealed class WebSessionStore(IOptions<WebProxySettings> settings, TimeProvider timeProvider, ILogger<WebSessionStore> logger) : IDisposable
{
    public const int MaxSessionsPerUser = 20;

    private readonly ConcurrentDictionary<string, WebSession> _sessions = new(StringComparer.Ordinal);

    public WebSession? TryCreate(Guid userId, Guid hostId, Uri target, string? pinnedFingerprint, string? embedderOrigin)
    {
        RemoveExpired();
        if (_sessions.Values.Count(s => s.UserId == userId) >= MaxSessionsPerUser)
        {
            return null;
        }

        var session = new WebSession(userId, hostId, target, pinnedFingerprint, embedderOrigin, timeProvider.GetUtcNow());
        _sessions[session.Id] = session;
        logger.LogInformation("Web session {SessionId} opened by user {UserId} for host {HostId} ({Target})", session.Id, userId, hostId, target);
        return session;
    }

    /// <summary>The live session with this id, or null when unknown or expired (expired ones are ended).</summary>
    public WebSession? Find(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        if (IsExpired(session, timeProvider.GetUtcNow()))
        {
            End(session.Id, "expired");
            return null;
        }

        return session;
    }

    public bool End(string id, string reason)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        logger.LogInformation("Web session {SessionId} of user {UserId} ended: {Reason}", id, session.UserId, reason);
        session.Dispose();
        return true;
    }

    /// <summary>Ends a session only if it belongs to the user (closing from the NKeeper tab).</summary>
    public bool EndOwned(string id, Guid userId) =>
        _sessions.TryGetValue(id, out var session) && session.UserId == userId && End(id, "closed by the user");

    /// <summary>Ends every session of a user (account disabled, deleted, demoted or password reset).</summary>
    public void EndAll(Guid userId)
    {
        foreach (var session in _sessions.Values.Where(s => s.UserId == userId).ToList())
        {
            End(session.Id, "account changed by an administrator");
        }
    }

    public void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in _sessions.Values.Where(s => IsExpired(s, now)).ToList())
        {
            End(session.Id, "expired");
        }
    }

    private bool IsExpired(WebSession session, DateTimeOffset now) =>
        now - session.LastSeenUtc > settings.Value.IdleTimeout || now - session.CreatedAtUtc > settings.Value.MaxLifetime;

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }
}

/// <summary>Ends idle and over-age web sessions once a minute.</summary>
internal sealed class WebSessionSweeper(WebSessionStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.RemoveExpired();
        }
    }
}
