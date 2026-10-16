using System.Collections.Concurrent;
using System.Security.Cryptography;
using Renci.SshNet;

namespace NKeeper.Api.Kvm;

/// <summary>
/// An open VM console: an SSH connection to the hypervisor with a tunnel to the VM's SPICE port,
/// bound on the API's loopback. Each SPICE channel the browser opens becomes one connection
/// through that tunnel.
/// </summary>
public sealed class SpiceSession : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _tunnel;
    private int _channels;

    internal SpiceSession(Guid userId, Guid hostId, string vmName, SshClient client, SpiceDisplay display, DateTimeOffset now)
    {
        Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        UserId = userId;
        HostId = hostId;
        VmName = vmName;
        Password = display.Password;
        _client = client;
        _tunnel = new ForwardedPortLocal("127.0.0.1", 0, display.Host, (uint)display.Port);
        _client.AddForwardedPort(_tunnel);
        _tunnel.Start();
        CreatedAtUtc = LastActivityUtc = now;
    }

    public string Id { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    public string VmName { get; }

    /// <summary>The VM's SPICE password, if it has one (the browser client needs it for SPICE's own login).</summary>
    public string? Password { get; }

    /// <summary>Loopback port of the tunnel to the VM's SPICE display.</summary>
    public int TunnelPort => (int)_tunnel.BoundPort;

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastActivityUtc { get; internal set; }

    public int OpenChannels => Volatile.Read(ref _channels);

    internal void ChannelOpened() => Interlocked.Increment(ref _channels);

    internal void ChannelClosed(DateTimeOffset now)
    {
        Interlocked.Decrement(ref _channels);
        LastActivityUtc = now;
    }

    public void Dispose()
    {
        try
        {
            _tunnel.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        _tunnel.Dispose();
        _client.Dispose();
    }
}

/// <summary>Open VM consoles, in memory (per API instance).</summary>
public sealed class SpiceSessionStore(TimeProvider timeProvider, ILogger<SpiceSessionStore> logger) : IDisposable
{
    public const int MaxSessionsPerUser = 10;

    /// <summary>A console with no open channel for this long is closed (e.g. the tab crashed).</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, SpiceSession> _sessions = new(StringComparer.Ordinal);

    internal SpiceSession? TryCreate(Guid userId, Guid hostId, string vmName, SshClient client, SpiceDisplay display)
    {
        RemoveExpired();
        if (_sessions.Values.Count(s => s.UserId == userId) >= MaxSessionsPerUser)
        {
            return null;
        }

        var session = new SpiceSession(userId, hostId, vmName, client, display, timeProvider.GetUtcNow());
        _sessions[session.Id] = session;
        logger.LogInformation("SPICE console {SessionId} opened by user {UserId} for VM {Vm} on host {HostId}", session.Id, userId, vmName, hostId);
        return session;
    }

    public SpiceSession? Find(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public bool End(string id, string reason)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        logger.LogInformation("SPICE console {SessionId} of user {UserId} ended: {Reason}", id, session.UserId, reason);
        session.Dispose();
        return true;
    }

    public bool EndOwned(string id, Guid userId) =>
        _sessions.TryGetValue(id, out var session) && session.UserId == userId && End(id, "closed by the user");

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
internal sealed class SpiceSessionSweeper(SpiceSessionStore store) : BackgroundService
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
