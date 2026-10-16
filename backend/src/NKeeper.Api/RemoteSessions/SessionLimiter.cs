using System.Collections.Concurrent;

namespace NKeeper.Api.RemoteSessions;

/// <summary>Caps concurrent remote sessions (SSH and RDP together) per user, in memory, per API instance.</summary>
public sealed class SessionLimiter
{
    public const int MaxSessionsPerUser = 20;

    private readonly ConcurrentDictionary<Guid, int> _sessions = new();

    public bool TryAcquire(Guid userId)
    {
        while (true)
        {
            var current = _sessions.GetOrAdd(userId, 0);
            if (current >= MaxSessionsPerUser)
            {
                return false;
            }

            if (_sessions.TryUpdate(userId, current + 1, current))
            {
                return true;
            }
        }
    }

    public void Release(Guid userId) => _sessions.AddOrUpdate(userId, 0, (_, count) => Math.Max(0, count - 1));
}
