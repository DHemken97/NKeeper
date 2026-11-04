using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Databases;

/// <summary>What a database server says about itself when a session opens.</summary>
/// <param name="ServerName">SQL Server's @@SERVERNAME, Oracle's instance (and database), ASE's @@servername.</param>
/// <param name="SessionId">The session's own ID on the server: SQL Server's and ASE's SPID, Oracle's SID.</param>
public sealed record DatabaseServerInfo(string ServerName, string ProductVersion, string? ProductLevel, string? Edition, string Login, int SessionId);

/// <summary>
/// A database tab: its own connection, which keeps the session's state (the current database or
/// schema, temporary tables, open transactions) between queries, as a query window in SSMS does. The
/// object explorer, scripting and the blocking manager use short connections of their own with the
/// same login, so they work while a query runs.
/// </summary>
public sealed class DatabaseSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly Func<CancellationToken, Task<DbConnection>> _openToolConnection;
    private readonly Action? _cleanup;
    private CancellationTokenSource? _running;

    /// <param name="openToolConnection">Opens another connection with the session's login.</param>
    /// <param name="cleanup">Releases what the session holds besides its connection (a jump host tunnel, a pinned certificate's file).</param>
    internal DatabaseSession(
        DatabaseEngine engine,
        Guid userId,
        Guid hostId,
        string display,
        DbConnection connection,
        DatabaseServerInfo server,
        Func<CancellationToken, Task<DbConnection>> openToolConnection,
        Action? cleanup,
        DateTimeOffset now)
    {
        Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Engine = engine;
        UserId = userId;
        HostId = hostId;
        Display = display;
        Connection = connection;
        Server = server;
        _openToolConnection = openToolConnection;
        _cleanup = cleanup;
        CreatedAtUtc = LastActivityUtc = now;
    }

    public string Id { get; }

    public DatabaseEngine Engine { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    /// <summary>e.g. <c>sa@10.0.0.5:1433</c>, for messages and the audit trail.</summary>
    public string Display { get; }

    public DatabaseServerInfo Server { get; }

    /// <summary>The query window's connection. Only use it while holding the lock (<see cref="TryBeginQuery"/>).</summary>
    internal DbConnection Connection { get; }

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
    internal Task<DbConnection> OpenToolConnectionAsync(CancellationToken cancellationToken) => _openToolConnection(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            Cancel();
            await Connection.DisposeAsync();
        }
        finally
        {
            // The jump tunnel and certificate file go even if closing the connection failed.
            _cleanup?.Invoke();
        }
    }

    internal sealed class QueryLease(DatabaseSession session, CancellationTokenSource cancel) : IDisposable
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

/// <summary>Open database sessions (SQL Server, Oracle, SAP ASE), in memory (per API instance).</summary>
public sealed class DatabaseSessionStore(TimeProvider timeProvider, Auditing.AuditLog audit, ILogger<DatabaseSessionStore> logger) : IAsyncDisposable
{
    /// <summary>Each query window is a session of its own (as in SSMS), plus one per tab for its explorer; across every kind of database.</summary>
    public const int MaxSessionsPerUser = 30;

    /// <summary>
    /// An open tab says it's there every minute; a session not heard from for this long is closed, even
    /// mid-query (the browser went away: crashed, asleep or off the network), so no connection is left behind.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DatabaseSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Counting a user's sessions and adding one happen together, so sessions opened at once can't pass the cap.</summary>
    private readonly Lock _adding = new();

    /// <summary>Adds a session, unless the user has too many open; the caller then disposes it.</summary>
    internal bool TryAdd(DatabaseSession session)
    {
        RemoveExpired();
        lock (_adding)
        {
            if (_sessions.Values.Count(s => s.UserId == session.UserId) >= MaxSessionsPerUser)
            {
                return false;
            }

            _sessions[session.Id] = session;
        }

        logger.LogInformation(
            "{Engine} session {SessionId} opened by user {UserId} to {Target} on host {HostId}", session.Engine, session.Id, session.UserId, session.Display, session.HostId);
        _ = audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SessionStart, session.HostId, $"{DatabaseEngines.Label(session.Engine)} · {session.Display}", session.Id, UserId: session.UserId));
        return true;
    }

    /// <summary>
    /// The user's own session of that kind of database; a session of someone else (or opened as another
    /// kind) is reported exactly like a missing one.
    /// </summary>
    internal DatabaseSession Get(string id, Guid userId, DatabaseEngine engine)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId || session.Engine != engine)
        {
            throw new SessionException(SessionErrorCodes.SessionNotFound, $"This {DatabaseEngines.Product(engine)} session has ended. Connect again.");
        }

        session.LastActivityUtc = timeProvider.GetUtcNow();
        return session;
    }

    /// <summary>The user's open sessions of one kind of database, oldest first (to work between two servers).</summary>
    internal IReadOnlyList<DatabaseSession> ListFor(Guid userId, DatabaseEngine engine) =>
        [.. _sessions.Values.Where(s => s.UserId == userId && s.Engine == engine).OrderBy(s => s.CreatedAtUtc)];

    public async Task<bool> EndAsync(string id, string reason)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        logger.LogInformation("{Engine} session {SessionId} of user {UserId} ended: {Reason}", session.Engine, id, session.UserId, reason);
        _ = audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SessionEnd,
            session.HostId,
            $"{DatabaseEngines.Label(session.Engine)} · {session.Display} · after {Auditing.AuditedSession.FormatDuration(timeProvider.GetUtcNow() - session.CreatedAtUtc)} · {reason}",
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

/// <summary>Closes idle and over-age database sessions once a minute.</summary>
internal sealed class DatabaseSessionSweeper(DatabaseSessionStore store) : BackgroundService
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
