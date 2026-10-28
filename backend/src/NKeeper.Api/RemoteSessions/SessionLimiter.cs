using System.Collections.Concurrent;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Api.RemoteSessions;

/// <summary>
/// Tracks open remote sessions (SSH and RDP together) per user, in memory, per API instance:
/// caps how many each user may hold, and lets administrators end them all at once (when an
/// account is disabled, deleted or its password reset).
/// </summary>
public sealed class SessionLimiter
{
    public const int MaxSessionsPerUser = 20;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, CancellationTokenSource>> _sessions = new();

    /// <summary>Registers a session, or returns null when the user is at the limit.</summary>
    /// <param name="terminated">Cancelled when <see cref="TerminateAll"/> is called for the user.</param>
    public IDisposable? TryStart(Guid userId, out CancellationToken terminated)
    {
        var sessions = _sessions.GetOrAdd(userId, _ => new());
        lock (sessions)
        {
            if (sessions.Count >= MaxSessionsPerUser)
            {
                terminated = CancellationToken.None;
                return null;
            }

            var id = Guid.NewGuid();
            var source = new CancellationTokenSource();
            sessions[id] = source;
            terminated = source.Token;
            return new Lease(() =>
            {
                if (sessions.TryRemove(id, out var removed))
                {
                    removed.Dispose();
                }
            });
        }
    }

    /// <summary>Ends every open session of the user.</summary>
    public void TerminateAll(Guid userId)
    {
        if (_sessions.TryGetValue(userId, out var sessions))
        {
            foreach (var source in sessions.Values)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ended concurrently.
                }
            }
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
