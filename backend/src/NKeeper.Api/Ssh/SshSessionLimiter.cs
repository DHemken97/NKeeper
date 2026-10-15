using System.Collections.Concurrent;

namespace NKeeper.Api.Ssh;

/// <summary>Caps concurrent terminal sessions per user (in memory, per API instance).</summary>
public sealed class SshSessionLimiter
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
