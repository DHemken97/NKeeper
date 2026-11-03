using Microsoft.Data.SqlClient;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// A session in a blocking chain. A lead blocker heads its chain: it blocks others but isn't itself
/// waiting on anyone. <see cref="Blocked"/> counts the sessions it blocks directly,
/// <see cref="BlockedVolume"/> every session waiting behind it down the chain.
/// </summary>
/// <param name="Database">Null when the session isn't in a database (shown as N/A).</param>
/// <param name="BlockedBy">The session it waits on; null for a lead blocker.</param>
/// <param name="WaitTimeMs">How long it has been waiting, for a blocked session.</param>
/// <param name="Sql">The batch it is running, or last ran (a sleeping lead blocker's open transaction began somewhere in it).</param>
/// <param name="Placeholder">A blocker SQL Server reports but that isn't a session (e.g. -2, an orphaned distributed transaction).</param>
public sealed record BlockingSession(
    int SessionId,
    string? Login,
    string? Host,
    string? Program,
    string? Database,
    string? Status,
    int OpenTransactions,
    int? BlockedBy,
    string? Command,
    string? WaitType,
    long? WaitTimeMs,
    string? WaitResource,
    string? Sql,
    int Blocked,
    int BlockedVolume,
    IReadOnlyList<BlockingSession> BlockedSessions,
    bool Placeholder = false);

/// <param name="Leads">The lead blockers, each with the sessions it blocks under it.</param>
/// <param name="CanSeeAllSessions">Whether the login has VIEW SERVER STATE; without it, only its own sessions are visible.</param>
public sealed record BlockingReport(IReadOnlyList<BlockingSession> Leads, int BlockedCount, bool CanSeeAllSessions, DateTimeOffset AtUtc);

/// <summary>The blocking manager: who blocks whom right now, as chains under their lead blockers; and KILL.</summary>
internal static class SqlBlocking
{
    /// <summary>Characters of a session's SQL sent.</summary>
    public const int MaxSqlChars = 4000;

    /// <summary>
    /// Every session that is blocked or blocking: its session, its request (when running), and its
    /// current or last batch.
    /// </summary>
    private const string Query =
        """
        WITH involved AS (
            SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0
            UNION
            SELECT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0
        )
        SELECT i.session_id, s.login_name, s.host_name, s.program_name,
               DB_NAME(COALESCE(r.database_id, s.database_id)), COALESCE(r.status, s.status),
               COALESCE(s.open_transaction_count, 0), NULLIF(r.blocking_session_id, 0), r.command,
               r.wait_type, CAST(r.wait_time AS bigint), r.wait_resource,
               CAST(LEFT(t.text, @maxSql) AS nvarchar(max))
        FROM involved i
        LEFT JOIN sys.dm_exec_sessions s ON s.session_id = i.session_id
        OUTER APPLY (SELECT TOP 1 * FROM sys.dm_exec_requests rq WHERE rq.session_id = i.session_id ORDER BY rq.request_id) r
        OUTER APPLY (SELECT TOP 1 most_recent_sql_handle FROM sys.dm_exec_connections c WHERE c.session_id = i.session_id) c
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t;

        SELECT CAST(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS bit);
        """;

    public static async Task<BlockingReport> ReadAsync(SqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = new List<BlockingSession>();
        bool canSeeAll;
        await using (var command = new SqlCommand(Query, connection) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("@maxSql", MaxSqlChars);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(new BlockingSession(
                    reader.GetInt16(0),
                    Text(1),
                    Text(2),
                    Text(3),
                    Text(4),
                    Text(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt16(7),
                    Text(8),
                    Text(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    Text(11),
                    Text(12),
                    0,
                    0,
                    [],
                    Placeholder: reader.IsDBNull(1) && reader.IsDBNull(5)));
            }

            await reader.NextResultAsync(cancellationToken);
            canSeeAll = await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0) && reader.GetBoolean(0);
        }

        var leads = BuildChains(rows);
        return new BlockingReport(leads, rows.Count(r => r.BlockedBy is not null), canSeeAll, now);
    }

    /// <summary>
    /// Arranges sessions into chains under their lead blockers (most blocked volume first). A session
    /// waiting on one that isn't listed, or a cycle with no head, is still shown: the missing blocker
    /// becomes a placeholder lead, and a cycle is headed by its lowest session ID.
    /// </summary>
    internal static IReadOnlyList<BlockingSession> BuildChains(IReadOnlyList<BlockingSession> sessions)
    {
        var byId = sessions.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First());
        foreach (var missing in sessions.Where(s => s.BlockedBy is { } b && !byId.ContainsKey(b)).Select(s => s.BlockedBy!.Value).Distinct().ToList())
        {
            byId[missing] = new BlockingSession(missing, null, null, null, null, missing < 0 ? Placeholder(missing) : null, 0, null, null, null, null, null, null, 0, 0, [], Placeholder: true);
        }

        var children = byId.Values.Where(s => s.BlockedBy is not null).ToLookup(s => s.BlockedBy!.Value);
        var placed = new HashSet<int>();

        BlockingSession Build(BlockingSession session, int? leadOf)
        {
            placed.Add(session.SessionId);
            var blocked = children[session.SessionId]
                .Where(c => !placed.Contains(c.SessionId))
                .OrderByDescending(c => c.WaitTimeMs ?? 0)
                .Select(c => Build(c, leadOf))
                .ToList();
            return session with
            {
                BlockedBy = session.SessionId == leadOf ? null : session.BlockedBy,
                Blocked = blocked.Count,
                BlockedVolume = blocked.Count + blocked.Sum(b => b.BlockedVolume),
                BlockedSessions = blocked,
            };
        }

        var leads = byId.Values
            .Where(s => s.BlockedBy is null && children[s.SessionId].Any())
            .OrderBy(s => s.SessionId)
            .Select(s => Build(s, s.SessionId))
            .ToList();

        // Sessions blocking each other in a circle (until the deadlock monitor steps in): head each by its lowest ID.
        foreach (var session in byId.Values.Where(s => s.BlockedBy is not null).OrderBy(s => s.SessionId))
        {
            if (!placed.Contains(session.SessionId))
            {
                leads.Add(Build(session, session.SessionId));
            }
        }

        return [.. leads.OrderByDescending(l => l.BlockedVolume).ThenBy(l => l.SessionId)];
    }

    private static string Placeholder(int sessionId) => sessionId switch
    {
        -2 => "orphaned distributed transaction",
        -3 => "deferred recovery transaction",
        -4 => "latch owner unknown",
        -5 => "asynchronous system task",
        _ => "not a session",
    };

    /// <summary>KILL takes no variables: the ID is a number, written in as one.</summary>
    public static async Task KillAsync(SqlConnection connection, int sessionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"KILL {sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
