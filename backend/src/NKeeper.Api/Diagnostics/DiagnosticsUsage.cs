using System.Diagnostics;
using System.Security.Claims;

namespace NKeeper.Api.Diagnostics;

/// <summary>
/// Totals of what gets used, written once a day and at shutdown: per endpoint and per session protocol,
/// never per user or per request. Only route patterns are kept, never the values in a URL. The users
/// themselves stay in memory: the file only says how many there were.
/// </summary>
public sealed class DiagnosticsUsage
{
    private const int MaxReasonsPerProtocol = 20;
    private const int MaxReasonLength = 200;
    private static readonly int[] BucketsMs = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10_000, 30_000, 60_000];

    private readonly Lock _gate = new();
    private readonly DiagnosticsLog _log;
    private readonly DiagnosticsRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, RouteTotals> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionTotals> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset _since;

    internal DiagnosticsUsage(DiagnosticsLog log, DiagnosticsRedactor redactor, TimeProvider timeProvider)
    {
        _log = log;
        _redactor = redactor;
        _time = timeProvider;
        _since = timeProvider.GetUtcNow();
    }

    /// <param name="route">The route pattern, e.g. "GET api/hosts/{id:guid}".</param>
    /// <param name="milliseconds">Null when the time says nothing about speed, as for a session's WebSocket.</param>
    public void RecordRequest(string route, int status, double? milliseconds, string? userId)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(route, out var totals))
            {
                _routes[route] = totals = new RouteTotals();
            }

            totals.Uses++;
            if (status == StatusCodes.Status499ClientClosedRequest)
            {
                totals.Cancelled++;
            }
            else if (status >= 500)
            {
                totals.ServerErrors++;
            }
            else if (status >= 400)
            {
                totals.ClientErrors++;
            }

            if (userId is not null)
            {
                totals.Users.Add(userId);
            }

            if (milliseconds is { } ms)
            {
                var bucket = Array.FindIndex(BucketsMs, limit => ms <= limit);
                totals.Timed[bucket < 0 ? BucketsMs.Length : bucket]++;
                totals.TimedCount++;
                totals.MaxMs = Math.Max(totals.MaxMs, ms);
            }
        }
    }

    public void SessionConnected(string protocol)
    {
        lock (_gate)
        {
            Session(protocol).Connected++;
        }
    }

    public void SessionFailed(string protocol, string reason)
    {
        var cleaned = Reason(reason);
        lock (_gate)
        {
            var totals = Session(protocol);
            totals.Failed++;
            CountReason(totals, cleaned);
        }
    }

    public void SessionEnded(string protocol, TimeSpan duration, string? reason)
    {
        var cleaned = reason is null ? null : Reason(reason);
        lock (_gate)
        {
            var totals = Session(protocol);
            totals.Ended++;
            totals.TotalSeconds += duration.TotalSeconds;
            if (cleaned is not null)
            {
                CountReason(totals, cleaned);
            }
        }
    }

    /// <summary>Writes the totals since the last time, and starts again.</summary>
    public void Flush()
    {
        List<KeyValuePair<string, RouteTotals>> routes;
        List<KeyValuePair<string, SessionTotals>> sessions;
        DateTimeOffset since;
        lock (_gate)
        {
            routes = [.. _routes];
            sessions = [.. _sessions];
            since = _since;
            _routes.Clear();
            _sessions.Clear();
            _since = _time.GetUtcNow();
        }

        foreach (var (route, totals) in routes.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            _log.Write("usage", new
            {
                sinceUtc = since,
                route,
                uses = totals.Uses,
                users = totals.Users.Count,
                clientErrors = totals.ClientErrors,
                serverErrors = totals.ServerErrors,
                cancelled = totals.Cancelled,
                p50Ms = totals.Percentile(0.50),
                p95Ms = totals.Percentile(0.95),
                maxMs = totals.TimedCount > 0 ? Math.Round(totals.MaxMs) : (double?)null,
            });
        }

        foreach (var (protocol, totals) in sessions.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            _log.Write("sessions", new
            {
                sinceUtc = since,
                protocol,
                connected = totals.Connected,
                failed = totals.Failed,
                ended = totals.Ended,
                averageSeconds = totals.Ended > 0 ? Math.Round(totals.TotalSeconds / totals.Ended) : (double?)null,
                reasons = totals.Reasons,
            });
        }
    }

    private static void CountReason(SessionTotals totals, string reason)
    {
        if (totals.Reasons.ContainsKey(reason) || totals.Reasons.Count < MaxReasonsPerProtocol)
        {
            totals.Reasons[reason] = totals.Reasons.GetValueOrDefault(reason) + 1;
        }
    }

    private string Reason(string reason)
    {
        var cleaned = _redactor.Redact(reason);
        return cleaned.Length > MaxReasonLength ? cleaned[..MaxReasonLength] + "…" : cleaned;
    }

    private SessionTotals Session(string protocol)
    {
        if (!_sessions.TryGetValue(protocol, out var totals))
        {
            _sessions[protocol] = totals = new SessionTotals();
        }

        return totals;
    }

    private sealed class RouteTotals
    {
        public long Uses { get; set; }

        public long ClientErrors { get; set; }

        public long ServerErrors { get; set; }

        public long Cancelled { get; set; }

        public HashSet<string> Users { get; } = new(StringComparer.Ordinal);

        public long[] Timed { get; } = new long[BucketsMs.Length + 1];

        public long TimedCount { get; set; }

        public double MaxMs { get; set; }

        /// <summary>The upper edge of the bucket it falls in, which is as close as it needs to be and takes no memory per request.</summary>
        public double? Percentile(double fraction)
        {
            if (TimedCount == 0)
            {
                return null;
            }

            var target = (long)Math.Ceiling(TimedCount * fraction);
            long seen = 0;
            for (var i = 0; i < BucketsMs.Length; i++)
            {
                seen += Timed[i];
                if (seen >= target)
                {
                    return Math.Min(BucketsMs[i], Math.Round(MaxMs));
                }
            }

            return Math.Round(MaxMs);
        }
    }

    private sealed class SessionTotals
    {
        public long Connected { get; set; }

        public long Failed { get; set; }

        public long Ended { get; set; }

        public double TotalSeconds { get; set; }

        public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Counts each API request by its route pattern, for <see cref="DiagnosticsUsage"/>.</summary>
internal sealed class DiagnosticsRequestMiddleware(RequestDelegate next, DiagnosticsLog log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!log.Enabled)
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        int? failedWith = null;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            failedWith = StatusCodes.Status499ClientClosedRequest;
            throw;
        }
        catch
        {
            failedWith = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } pattern } && pattern.TrimStart('/').StartsWith("api/", StringComparison.Ordinal))
            {
                var status = failedWith ?? (context.RequestAborted.IsCancellationRequested ? StatusCodes.Status499ClientClosedRequest : context.Response.StatusCode);
                log.Usage.RecordRequest(
                    $"{context.Request.Method} {pattern}",
                    status,
                    context.WebSockets.IsWebSocketRequest ? null : Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            }
        }
    }
}
