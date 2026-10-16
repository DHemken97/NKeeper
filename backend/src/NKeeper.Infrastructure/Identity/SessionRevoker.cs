using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Infrastructure.Identity;

/// <summary>Ends a user's server-side sessions, so copied or open cookies stop working at once.</summary>
public sealed class SessionRevoker(NKeeperDbContext db)
{
    /// <param name="exceptSessionKey">A session to keep (the caller's own); see <c>CurrentSessionKey</c>.</param>
    public Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken, string? exceptSessionKey = null)
    {
        var sessions = db.AuthSessions.Where(s => s.UserId == userId);
        if (Guid.TryParseExact(exceptSessionKey, "N", out var keep))
        {
            sessions = sessions.Where(s => s.Id != keep);
        }

        return sessions.ExecuteDeleteAsync(cancellationToken);
    }
}
