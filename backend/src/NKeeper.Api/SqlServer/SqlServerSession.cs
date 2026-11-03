using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.SqlServer;

/// <summary>What a SQL Server says about itself when a session opens.</summary>
public sealed record SqlServerInfo(string ServerName, string ProductVersion, string? ProductLevel, string? Edition, string Login, int SessionId);

/// <summary>
/// A SQL Server tab: its own connection, which keeps the session's state (the current database,
/// temporary tables, open transactions) between queries, as a query window in SSMS does. The object
/// explorer, scripting and the blocking manager use short connections of their own with the same
/// login, so they work while a query runs.
/// </summary>
public sealed class SqlServerSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly JumpTunnel? _tunnel;
    private readonly string? _certificateFile;
    private CancellationTokenSource? _running;

    internal SqlServerSession(
        Guid userId, Guid hostId, string display, SqlConnectionStringBuilder settings, SqlConnection connection, SqlServerInfo server, JumpTunnel? tunnel, string? certificateFile, DateTimeOffset now)
    {
        Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        UserId = userId;
        HostId = hostId;
        Display = display;
        Settings = settings;
        Connection = connection;
        Server = server;
        _tunnel = tunnel;
        _certificateFile = certificateFile;
        CreatedAtUtc = LastActivityUtc = now;
    }

    public string Id { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    /// <summary>e.g. <c>sa@10.0.0.5:1433</c>, for messages and the audit trail.</summary>
    public string Display { get; }

    public SqlServerInfo Server { get; }

    /// <summary>How to connect again (includes the password: it stays in this process's memory).</summary>
    internal SqlConnectionStringBuilder Settings { get; }

    /// <summary>The query window's connection. Only use it while holding the lock (<see cref="TryBeginQuery"/>).</summary>
    internal SqlConnection Connection { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastActivityUtc { get; internal set; }

    public bool IsRunning => Volatile.Read(ref _running) is not null;

    /// <summary>
    /// Claims the query connection for one query, with a token that <see cref="Cancel"/> trips; null
    /// when a query is already running. Dispose the result to release it.
    /// </summary>
    internal QueryLease? TryBeginQuery(CancellationToken requestAborted)
    {
        if (!_queryLock.Wait(0, CancellationToken.None))
        {
            return null;
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        Volatile.Write(ref _running, cancel);
        return new QueryLease(this, cancel);
    }

    /// <summary>Cancels the running query, if there is one.</summary>
    internal bool Cancel()
    {
        var running = Volatile.Read(ref _running);
        if (running is null)
        {
            return false;
        }

        try
        {
            running.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished meanwhile.
        }

        return true;
    }

    /// <summary>A new connection with the session's login, for explorer, scripting and blocking requests.</summary>
    internal async Task<SqlConnection> OpenToolConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(Settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await Connection.DisposeAsync();
        _tunnel?.Dispose();
        if (_certificateFile is not null)
        {
            try
            {
                File.Delete(_certificateFile);
            }
            catch (IOException)
            {
                // A temporary file of a public certificate: nothing to worry about if it stays.
            }
        }
    }

    internal sealed class QueryLease(SqlServerSession session, CancellationTokenSource cancel) : IDisposable
    {
        public CancellationToken Token => cancel.Token;

        public void Dispose()
        {
            Volatile.Write(ref session._running, null);
            cancel.Dispose();
            session._queryLock.Release();
        }
    }
}

/// <summary>Open SQL Server sessions, in memory (per API instance).</summary>
public sealed class SqlServerSessionStore(TimeProvider timeProvider, Auditing.AuditLog audit, ILogger<SqlServerSessionStore> logger) : IAsyncDisposable
{
    /// <summary>Each query window is a session of its own (as in SSMS), plus one per tab for its explorer.</summary>
    public const int MaxSessionsPerUser = 30;

    /// <summary>
    /// An open tab says it's there every minute; a session not heard from for this long is closed, even
    /// mid-query (the browser went away: crashed, asleep or off the network), so no connection is left behind.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, SqlServerSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Adds a session, unless the user has too many open; the caller then disposes it.</summary>
    internal bool TryAdd(SqlServerSession session)
    {
        RemoveExpired();
        if (_sessions.Values.Count(s => s.UserId == session.UserId) >= MaxSessionsPerUser)
        {
            return false;
        }

        _sessions[session.Id] = session;
        logger.LogInformation("SQL Server session {SessionId} opened by user {UserId} to {Target} on host {HostId}", session.Id, session.UserId, session.Display, session.HostId);
        _ = audit.RecordAsync(new Auditing.AuditEntry(Auditing.AuditActions.SessionStart, session.HostId, $"MS SQL · {session.Display}", session.Id, UserId: session.UserId));
        return true;
    }

    /// <summary>The user's own session; a session of someone else is reported exactly like a missing one.</summary>
    internal SqlServerSession Get(string id, Guid userId)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId)
        {
            throw new SessionException(SessionErrorCodes.SessionNotFound, "This SQL Server session has ended. Connect again.");
        }

        session.LastActivityUtc = timeProvider.GetUtcNow();
        return session;
    }

    public async Task<bool> EndAsync(string id, string reason)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        logger.LogInformation("SQL Server session {SessionId} of user {UserId} ended: {Reason}", id, session.UserId, reason);
        _ = audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SessionEnd,
            session.HostId,
            $"MS SQL · {session.Display} · after {Auditing.AuditedSession.FormatDuration(timeProvider.GetUtcNow() - session.CreatedAtUtc)} · {reason}",
            session.Id,
            UserId: session.UserId));
        await session.DisposeAsync();
        return true;
    }

    public void EndAll(Guid userId)
    {
        foreach (var session in _sessions.Values.Where(s => s.UserId == userId).ToList())
        {
            _ = EndAsync(session.Id, "account changed by an administrator");
        }
    }

    public void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in _sessions.Values.Where(s =>
            now - s.LastActivityUtc > IdleTimeout || now - s.CreatedAtUtc > MaxLifetime).ToList())
        {
            _ = EndAsync(session.Id, "expired");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}

/// <summary>Closes idle and over-age SQL Server sessions once a minute.</summary>
internal sealed class SqlServerSessionSweeper(SqlServerSessionStore store) : BackgroundService
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
