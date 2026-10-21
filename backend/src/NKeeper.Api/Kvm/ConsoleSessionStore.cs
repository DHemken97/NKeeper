using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NKeeper.Api.Kvm;

/// <summary>
/// An open VM console (SPICE or VNC): an SSH connection to the hypervisor and the way through it to
/// the VM's display (a tunnel to its port, or Proxmox's VNC proxy). Each channel the browser opens
/// (SPICE uses several, VNC one) becomes one connection through it.
/// </summary>
public sealed class ConsoleSession : IDisposable
{
    private int _channels;

    internal ConsoleSession(Guid userId, Guid hostId, string vmName, string protocol, string? password, ConsoleEndpoint endpoint, DateTimeOffset now)
    {
        Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        UserId = userId;
        HostId = hostId;
        VmName = vmName;
        Protocol = protocol;
        Password = password;
        Endpoint = endpoint;
        CreatedAtUtc = LastActivityUtc = now;
    }

    public string Id { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    public string VmName { get; }

    /// <summary>One of <see cref="ConsoleProtocols"/>.</summary>
    public string Protocol { get; }

    /// <summary>The VM's display password, if it has one (the browser client needs it for the protocol's own login).</summary>
    public string? Password { get; }

    /// <summary>How channels reach the VM's display.</summary>
    internal ConsoleEndpoint Endpoint { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastActivityUtc { get; internal set; }

    public int OpenChannels => Volatile.Read(ref _channels);

    internal void ChannelOpened() => Interlocked.Increment(ref _channels);

    internal void ChannelClosed(DateTimeOffset now)
    {
        Interlocked.Decrement(ref _channels);
        LastActivityUtc = now;
    }

    public void Dispose() => Endpoint.Dispose();
}

/// <summary>Open VM consoles, in memory (per API instance).</summary>
public sealed class ConsoleSessionStore(TimeProvider timeProvider, Auditing.AuditLog audit, ILogger<ConsoleSessionStore> logger) : IDisposable
{
    public const int MaxSessionsPerUser = 10;

    /// <summary>A console with no open channel for this long is closed (e.g. the tab crashed).</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, ConsoleSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Opens a console that takes ownership of <paramref name="endpoint"/>, unless the user has too many.</summary>
    internal ConsoleSession? TryCreate(Guid userId, Guid hostId, string vmName, string protocol, string? password, ConsoleEndpoint endpoint)
    {
        RemoveExpired();
        if (_sessions.Values.Count(s => s.UserId == userId) >= MaxSessionsPerUser)
        {
            return null;
        }

        var session = new ConsoleSession(userId, hostId, vmName, protocol, password, endpoint, timeProvider.GetUtcNow());
        _sessions[session.Id] = session;
        logger.LogInformation(
            "{Protocol} console {SessionId} opened by user {UserId} for VM {Vm} on host {HostId}",
            ConsoleProtocols.DisplayName(session.Protocol), session.Id, userId, vmName, hostId);
        _ = audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SessionStart, hostId, $"{ConsoleProtocols.DisplayName(session.Protocol)} console · VM {vmName}", session.Id, UserId: userId));
        return session;
    }

    public ConsoleSession? Find(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public bool End(string id, string reason)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        logger.LogInformation(
            "{Protocol} console {SessionId} of user {UserId} ended: {Reason}", ConsoleProtocols.DisplayName(session.Protocol), id, session.UserId, reason);
        _ = audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SessionEnd,
            session.HostId,
            $"{ConsoleProtocols.DisplayName(session.Protocol)} console · VM {session.VmName} · after {Auditing.AuditedSession.FormatDuration(timeProvider.GetUtcNow() - session.CreatedAtUtc)} · {reason}",
            session.Id,
            UserId: session.UserId));
        session.Dispose();
        return true;
    }

    /// <summary>Ends the user's own console of the given protocol.</summary>
    public bool EndOwned(string id, Guid userId, string protocol) =>
        _sessions.TryGetValue(id, out var session) && session.UserId == userId && session.Protocol == protocol && End(id, "closed by the user");

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
        foreach (var session in _sessions.Values.Where(s =>
            (s.OpenChannels == 0 && now - s.LastActivityUtc > IdleTimeout) || now - s.CreatedAtUtc > MaxLifetime).ToList())
        {
            End(session.Id, "expired");
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }
}

/// <summary>Closes idle and over-age VM consoles once a minute.</summary>
internal sealed class ConsoleSessionSweeper(ConsoleSessionStore store) : BackgroundService
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
