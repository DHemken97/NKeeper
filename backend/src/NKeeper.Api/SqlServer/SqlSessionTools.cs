using System.Globalization;
using Microsoft.Data.SqlClient;
using NKeeper.Api.Databases;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// SQL Server's session reports:
/// <list type="bullet">
/// <item>Session Health: sessions sleeping with a transaction open (abandoned by their client), orphaned and
/// in-doubt MSDTC transactions (the locks of session -2), killed sessions still rolling back, and sessions by client.</item>
/// <item>Remote Servers: linked servers, how logins map to them, the remote timeouts, sessions waiting on a remote
/// server now, remote waits since start, and MSDTC transactions.</item>
/// <item>Oracle Gateway: sessions an Oracle Database Gateway (DG4MSQL, DG4ODBC) opened here, the locks they hold and whom they block.</item>
/// </list>
/// They read the DMVs, so the login needs VIEW SERVER STATE (without it, only its own sessions show).
/// KILL is offered as a script to run, never run by the report.
/// </summary>
internal static class SqlSessionTools
{
    public const string Sessions = "sessions";
    public const string Remote = "remote";
    public const string Gateway = "gateway";

    private const int RowsPerResult = 500;
    private const int MaxScripts = 20;
    private const int MaxWarningsPerKind = 8;

    /// <summary>A session idle this long with a transaction open is worth a warning even when it blocks no one.</summary>
    private const int IdleWarningSeconds = 60;

    public static IReadOnlyList<SqlObjectNode> ToolNodes =>
    [
        new() { Kind = Kinds.Tool, Label = "Session Health", Detail = "Abandoned and orphaned sessions", Name = Sessions },
        new() { Kind = Kinds.Tool, Label = "Remote Servers", Detail = "Linked servers and MSDTC", Name = Remote },
        new() { Kind = Kinds.Tool, Label = "Oracle Gateway", Detail = "Sessions from DG4MSQL / DG4ODBC", Name = Gateway },
    ];

    public static Task<ToolReport> ReportAsync(SqlConnection connection, string tool, DateTimeOffset now, CancellationToken cancellationToken) => tool switch
    {
        Sessions => SessionsAsync(connection, now, cancellationToken),
        Remote => RemoteAsync(connection, now, cancellationToken),
        Gateway => GatewayAsync(connection, now, cancellationToken),
        _ => throw new SqlExplorerException($"There's no “{tool}” report."),
    };

    /// <summary>
    /// What a gateway's sessions look like: DG4MSQL and the older TG4MSQL name themselves; DG4ODBC and HSODBC take
    /// whatever the ODBC DSN's APP= says, so that is worth setting to something with "gateway" or "oracle" in it.
    /// </summary>
    internal static readonly string[] GatewayProgramPatterns = ["%dg4%", "%tg4%", "%hsodbc%", "%oracle%", "%gateway%", "%extproc%"];

    private static string GatewayPredicate(string alias) =>
        "(" + string.Join(" OR ", GatewayProgramPatterns.Select(p => $"{alias}.program_name LIKE N'{p}'")) + ")";

    /// <summary>One row per user session (not this report's own), with what it holds and how long it has been idle.</summary>
    private static string SessionDetail(string where, string orderBy = "[Idle s] DESC") =>
        $"""
        SELECT s.session_id AS [Session], s.login_name AS [Login], s.host_name AS [Host], c.client_net_address AS [Address],
               s.host_process_id AS [Client PID], s.program_name AS [Program], DB_NAME(s.database_id) AS [Database], s.status AS [Status],
               s.open_transaction_count AS [Open trans],
               CASE WHEN s.status = 'sleeping' THEN DATEDIFF(SECOND, s.last_request_end_time, GETDATE()) END AS [Idle s],
               tr.began AS [Transaction began], DATEDIFF(SECOND, tr.began, GETDATE()) AS [Transaction s],
               (SELECT COUNT(*) FROM sys.dm_tran_locks l WHERE l.request_session_id = s.session_id AND l.resource_type <> 'DATABASE') AS [Locks],
               (SELECT COUNT(*) FROM sys.dm_exec_requests b WHERE b.blocking_session_id = s.session_id AND b.session_id <> s.session_id) AS [Blocking],
               (SELECT TOP 1 NULLIF(r.blocking_session_id, 0) FROM sys.dm_exec_requests r WHERE r.session_id = s.session_id) AS [Blocked by],
               CASE s.transaction_isolation_level WHEN 1 THEN 'Read uncommitted' WHEN 2 THEN 'Read committed' WHEN 3 THEN 'Repeatable read'
                    WHEN 4 THEN 'Serializable' WHEN 5 THEN 'Snapshot' ELSE 'Unspecified' END AS [Isolation],
               s.login_time AS [Logged in],
               CAST(LEFT(t.text, 4000) AS nvarchar(4000)) AS [Last SQL]
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
        OUTER APPLY (SELECT MIN(a.transaction_begin_time) AS began
                     FROM sys.dm_tran_session_transactions st JOIN sys.dm_tran_active_transactions a ON a.transaction_id = st.transaction_id
                     WHERE st.session_id = s.session_id) tr
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
        WHERE s.is_user_process = 1 AND s.session_id <> @@SPID AND {where}
        ORDER BY {orderBy}
        """;

    /// <summary>Distributed (MSDTC) transactions: an orphaned one has no session, and its locks belong to session -2.</summary>
    private const string DtcSql =
        """
        SELECT a.transaction_id AS [Transaction], a.transaction_uow AS [UOW], a.name AS [Name], a.transaction_begin_time AS [Began],
               DATEDIFF(SECOND, a.transaction_begin_time, GETDATE()) AS [Age s],
               CASE a.dtc_state WHEN 1 THEN 'Active' WHEN 2 THEN 'Prepared' WHEN 3 THEN 'Committed' WHEN 4 THEN 'Aborted' WHEN 5 THEN 'Recovered'
                    ELSE CAST(a.dtc_state AS varchar(10)) END AS [DTC state],
               st.session_id AS [Session],
               CASE WHEN st.session_id IS NULL THEN 'Yes' ELSE 'No' END AS [Orphaned],
               (SELECT COUNT(*) FROM sys.dm_tran_locks l WHERE l.request_owner_guid = a.transaction_uow AND l.request_session_id = -2) AS [Orphaned locks]
        FROM sys.dm_tran_active_transactions a
        LEFT JOIN sys.dm_tran_session_transactions st ON st.transaction_id = a.transaction_id
        WHERE a.transaction_type = 4 OR (a.transaction_uow IS NOT NULL AND a.transaction_uow <> '00000000-0000-0000-0000-000000000000')
           OR a.transaction_uow IN (SELECT request_owner_guid FROM sys.dm_tran_locks WHERE request_session_id = -2)
        ORDER BY a.transaction_begin_time
        """;

    // ---- Session Health ----

    private static async Task<ToolReport> SessionsAsync(SqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var idle = await SectionAsync(
            connection,
            "Sleeping with a transaction open",
            "sys.dm_exec_sessions, sys.dm_tran_session_transactions, sys.dm_tran_locks",
            SessionDetail("s.status = 'sleeping' AND s.open_transaction_count > 0"),
            cancellationToken,
            "Its client ran something and never committed or rolled back: its locks stay until it does, or the session is killed. "
            + "A pooled connection returned with a transaction open, or a client that died without closing, looks like this.");
        warnings.AddRange(IdleWarnings(idle, "Session"));
        idle = idle with { Scripts = KillScripts(idle, all: true) };

        var dtc = await DtcSectionAsync(connection, warnings, cancellationToken);

        var killed = await SectionAsync(
            connection,
            "Killed, still rolling back",
            "sys.dm_exec_requests (KILLED/ROLLBACK)",
            """
            SELECT r.session_id AS [Session], s.login_name AS [Login], s.host_name AS [Host], s.program_name AS [Program],
                   DB_NAME(r.database_id) AS [Database], r.percent_complete AS [% done],
                   r.estimated_completion_time / 1000 AS [Seconds left], r.wait_type AS [Wait],
                   r.total_elapsed_time / 1000 AS [Running s]
            FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            WHERE r.command = 'KILLED/ROLLBACK'
            ORDER BY r.total_elapsed_time DESC
            """,
            cancellationToken,
            "A session killed while in a distributed query or transaction can sit at 0% waiting on the remote side "
            + "(an OLEDB, DTC or PREEMPTIVE wait): it finishes once the remote answers or its connection drops.");
        var stuck = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(killed))
        {
            var id = ToolReports.Cell(columns, row, "Session");
            var wait = ToolReports.Cell(columns, row, "Wait");
            warnings.Add($"Session {id} was killed and is still rolling back ({ToolReports.Cell(columns, row, "% done") ?? "0"}% done{(wait is null ? string.Empty : $", waiting on {wait}")}).");
            if (stuck.Count < MaxScripts)
            {
                stuck.Add(new ToolScript($"Status of {id}", $"KILL {id} WITH STATUSONLY;"));
            }
        }

        killed = killed with { Scripts = stuck };

        var clients = await SectionAsync(
            connection,
            "Sessions by client",
            "sys.dm_exec_sessions, sys.dm_exec_connections",
            """
            SELECT s.host_name AS [Host], c.client_net_address AS [Address], s.program_name AS [Program], s.login_name AS [Login],
                   COUNT(*) AS [Sessions],
                   SUM(CASE WHEN s.status = 'sleeping' THEN 1 ELSE 0 END) AS [Sleeping],
                   SUM(CASE WHEN s.status = 'sleeping' AND s.last_request_end_time < DATEADD(HOUR, -1, GETDATE()) THEN 1 ELSE 0 END) AS [Idle over 1 h],
                   SUM(CASE WHEN s.open_transaction_count > 0 THEN 1 ELSE 0 END) AS [With open trans],
                   MAX(CASE WHEN s.status = 'sleeping' THEN DATEDIFF(MINUTE, s.last_request_end_time, GETDATE()) END) AS [Longest idle min],
                   MIN(s.login_time) AS [Oldest login]
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
            WHERE s.is_user_process = 1
            GROUP BY s.host_name, c.client_net_address, s.program_name, s.login_name
            ORDER BY COUNT(*) DESC
            """,
            cancellationToken,
            "Many sessions idle for hours from one client is usually a connection leak, or a pool that never shrinks.");
        foreach (var (columns, row) in ToolReports.Rows(clients))
        {
            var stale = ToolReports.Number(columns, row, "Idle over 1 h") ?? 0;
            if (stale >= 50)
            {
                warnings.Add($"{stale:0} sessions from {Who(columns, row)} have been idle for over an hour: a connection leak?");
            }
        }

        var noConnection = await SectionAsync(
            connection,
            "Sessions without a connection",
            "sys.dm_exec_sessions without sys.dm_exec_connections",
            """
            SELECT s.session_id AS [Session], s.login_name AS [Login], s.host_name AS [Host], s.program_name AS [Program], s.status AS [Status],
                   s.open_transaction_count AS [Open trans], s.login_time AS [Logged in], s.last_request_end_time AS [Last request ended]
            FROM sys.dm_exec_sessions s
            WHERE s.is_user_process = 1 AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_connections c WHERE c.session_id = s.session_id)
            ORDER BY s.session_id
            """,
            cancellationToken,
            "Its client has gone but the session hasn't finished ending: usually it's rolling back, or waiting on a remote server.");
        foreach (var (columns, row) in ToolReports.Rows(noConnection))
        {
            if ((ToolReports.Number(columns, row, "Open trans") ?? 0) > 0)
            {
                warnings.Add($"Session {ToolReports.Cell(columns, row, "Session")} ({Who(columns, row)}) has no connection but still has a transaction open.");
            }
        }

        return new ToolReport(warnings, [], [idle, dtc, killed, clients, noConnection], now);
    }

    // ---- Remote Servers ----

    private static async Task<ToolReport> RemoteAsync(SqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var settings = await SectionAsync(
            connection,
            "Server settings",
            "sys.configurations",
            """
            SELECT name AS [Setting], CAST(value_in_use AS int) AS [In use], CAST(value AS int) AS [Configured], description AS [Description]
            FROM sys.configurations
            WHERE name IN ('remote query timeout (s)', 'remote login timeout (s)', 'remote access', 'remote proc trans',
                           'Ad Hoc Distributed Queries', 'remote admin connections', 'user connections')
            ORDER BY name
            """,
            cancellationToken);
        var remoteQueryTimeout = 600;
        foreach (var (columns, row) in ToolReports.Rows(settings))
        {
            var name = ToolReports.Cell(columns, row, "Setting");
            var value = (int)(ToolReports.Number(columns, row, "In use") ?? 0);
            if (string.Equals(name, "remote query timeout (s)", StringComparison.OrdinalIgnoreCase))
            {
                remoteQueryTimeout = value;
                if (value == 0)
                {
                    warnings.Add("“remote query timeout (s)” is 0: a query on a linked server waits forever, so a remote that stops answering holds the session (and its locks) here until it's killed.");
                }
            }
            else if (string.Equals(name, "remote proc trans", StringComparison.OrdinalIgnoreCase) && value == 1)
            {
                warnings.Add("“remote proc trans” is on: every remote procedure call made inside a transaction is promoted to an MSDTC transaction.");
            }
        }

        var servers = await SectionAsync(
            connection,
            "Linked servers",
            "sys.servers",
            """
            SELECT s.name AS [Linked server], s.product AS [Product], s.provider AS [Provider], s.data_source AS [Data source],
                   s.catalog AS [Catalog], s.is_data_access_enabled AS [Data access], s.is_rpc_out_enabled AS [RPC out],
                   s.is_remote_proc_transaction_promotion_enabled AS [Promotes to DTC], s.lazy_schema_validation AS [Lazy schema],
                   s.connect_timeout AS [Connect timeout s], s.query_timeout AS [Query timeout s], s.modify_date AS [Changed]
            FROM sys.servers s
            WHERE s.is_linked = 1
            ORDER BY s.name
            """,
            cancellationToken,
            "A timeout of 0 uses the server's setting (“remote login timeout”, “remote query timeout” above). "
            + "Linked servers to Oracle through a gateway hold their remote transaction until the local one ends.");
        var tests = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(servers))
        {
            var name = ToolReports.Cell(columns, row, "Linked server");
            if (name is null)
            {
                continue;
            }

            if ((ToolReports.Number(columns, row, "Query timeout s") ?? 0) == 0 && remoteQueryTimeout == 0)
            {
                warnings.Add($"Linked server {name} has no query timeout (its own is 0, and so is the server's).");
            }

            if (tests.Count < MaxScripts)
            {
                tests.Add(new ToolScript($"Test {name}", $"EXEC master.sys.sp_testlinkedserver {Literal(name)};"));
            }
        }

        servers = servers with { Scripts = tests };

        var logins = await SectionAsync(
            connection,
            "Login mappings",
            "sys.linked_logins",
            """
            SELECT s.name AS [Linked server], COALESCE(p.name, N'(every other login)') AS [Local login],
                   CASE WHEN l.uses_self_credential = 1 THEN N'(its own credentials)' ELSE COALESCE(l.remote_name, N'(no security context)') END AS [Connects as],
                   l.modify_date AS [Changed]
            FROM sys.linked_logins l
            JOIN sys.servers s ON s.server_id = l.server_id
            LEFT JOIN sys.server_principals p ON p.principal_id = l.local_principal_id
            WHERE s.is_linked = 1
            ORDER BY s.name, CASE WHEN p.name IS NULL THEN 1 ELSE 0 END, p.name
            """,
            cancellationToken);

        var waiting = await SectionAsync(
            connection,
            "Waiting on a remote server now",
            "sys.dm_exec_requests (OLEDB, MSQL_DQ, DTC and PREEMPTIVE waits)",
            """
            SELECT r.session_id AS [Session], s.login_name AS [Login], s.host_name AS [Host], s.program_name AS [Program],
                   DB_NAME(r.database_id) AS [Database], r.command AS [Command], r.wait_type AS [Wait],
                   r.total_elapsed_time / 1000 AS [Running s], NULLIF(r.blocking_session_id, 0) AS [Blocked by],
                   s.open_transaction_count AS [Open trans],
                   (SELECT COUNT(*) FROM sys.dm_exec_requests b WHERE b.blocking_session_id = r.session_id AND b.session_id <> r.session_id) AS [Blocking],
                   CAST(LEFT(t.text, 4000) AS nvarchar(4000)) AS [SQL]
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id <> @@SPID
              AND (r.wait_type IN ('OLEDB', 'MSQL_DQ') OR r.wait_type LIKE 'DTC%' OR r.wait_type LIKE 'PREEMPTIVE_OLEDB%'
                   OR r.wait_type LIKE 'PREEMPTIVE_DTC%' OR r.wait_type LIKE 'PREEMPTIVE_COM%')
            ORDER BY r.total_elapsed_time DESC
            """,
            cancellationToken,
            "OLEDB is a linked server's provider working (or waiting on the network); DTC waits are MSDTC deciding a transaction's outcome.");
        foreach (var (columns, row) in ToolReports.Rows(waiting))
        {
            var blocking = ToolReports.Number(columns, row, "Blocking") ?? 0;
            var running = ToolReports.Number(columns, row, "Running s") ?? 0;
            if (blocking > 0 || running >= 300)
            {
                warnings.Add(
                    $"Session {ToolReports.Cell(columns, row, "Session")} has been waiting on {ToolReports.Cell(columns, row, "Wait")} for {Duration(running)}"
                    + (blocking > 0 ? $", blocking {blocking:0} {(blocking == 1 ? "session" : "sessions")}." : "."));
            }
        }

        waiting = waiting with { Scripts = KillScripts(waiting, all: false) };

        var dtc = await DtcSectionAsync(connection, warnings, cancellationToken);

        var waits = await SectionAsync(
            connection,
            "Remote waits since the server started",
            "sys.dm_os_wait_stats",
            """
            SELECT wait_type AS [Wait], waiting_tasks_count AS [Waits], wait_time_ms / 1000 AS [Total s],
                   CAST(wait_time_ms * 1.0 / NULLIF(waiting_tasks_count, 0) AS decimal(18, 1)) AS [Average ms], max_wait_time_ms / 1000 AS [Longest s]
            FROM sys.dm_os_wait_stats
            WHERE waiting_tasks_count > 0
              AND (wait_type IN ('OLEDB', 'MSQL_DQ') OR wait_type LIKE 'DTC%' OR wait_type LIKE 'PREEMPTIVE_OLEDB%' OR wait_type LIKE 'PREEMPTIVE_DTC%')
            ORDER BY wait_time_ms DESC
            """,
            cancellationToken);

        return new ToolReport(warnings, [], [settings, servers, logins, waiting, dtc, waits], now);
    }

    // ---- Oracle Gateway ----

    private static async Task<ToolReport> GatewayAsync(SqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var patterns = string.Join(", ", GatewayProgramPatterns.Select(p => p.Trim('%')));

        var sessions = await SectionAsync(
            connection,
            "Gateway sessions",
            "sys.dm_exec_sessions (program name like " + patterns + ")",
            SessionDetail(GatewayPredicate("s"), "s.open_transaction_count DESC, [Idle s] DESC"),
            cancellationToken,
            "Oracle starts a transaction on the remote side for any statement through a database link, even a SELECT, and the gateway keeps it "
            + "open here until the Oracle session commits or rolls back. A sleeping session with a transaction is one waiting on Oracle. "
            + "Client PID is the gateway agent's process: it's “Process” in the Oracle side's Database Links report (v$hs_agent). "
            + "On the gateway, HS_IDLE_TIMEOUT ends idle agents, and HS_FDS_TRANSACTION_ISOLATION sets the isolation they run in. "
            + "DG4ODBC sessions are named by the DSN's APP=: if yours aren't listed, look for its host under Session Health's “Sessions by client”.");

        var count = 0;
        var serializable = new List<string>();
        foreach (var (columns, row) in ToolReports.Rows(sessions))
        {
            count++;
            var isolation = ToolReports.Cell(columns, row, "Isolation");
            if (isolation is "Serializable" or "Repeatable read" && serializable.Count < MaxWarningsPerKind)
            {
                serializable.Add(ToolReports.Cell(columns, row, "Session") ?? "?");
            }
        }

        warnings.AddRange(IdleWarnings(sessions, "Gateway session"));
        foreach (var (columns, row) in ToolReports.Rows(sessions))
        {
            if ((ToolReports.Number(columns, row, "Open trans") ?? 0) == 0 && (ToolReports.Number(columns, row, "Blocking") ?? 0) > 0)
            {
                warnings.Add($"Gateway session {ToolReports.Cell(columns, row, "Session")} is blocking {ToolReports.Cell(columns, row, "Blocking")} while running.");
            }
        }

        if (serializable.Count > 0)
        {
            warnings.Add($"Gateway sessions {string.Join(", ", serializable)} run in serializable or repeatable read: even their reads hold locks until Oracle commits (see HS_FDS_TRANSACTION_ISOLATION).");
        }

        if (sessions.Error is null && count == 0)
        {
            sessions = sessions with { Note = $"No session's program name looks like a gateway ({patterns}). {sessions.Note}" };
        }

        sessions = sessions with { Scripts = KillScripts(sessions, all: false, idleWithTransaction: true) };

        var gatewaySessions = $"SELECT s.session_id FROM sys.dm_exec_sessions s WHERE s.is_user_process = 1 AND {GatewayPredicate("s")}";

        var locks = await SectionAsync(
            connection,
            "Locks they hold",
            "sys.dm_tran_locks",
            $"""
            SELECT l.request_session_id AS [Session], DB_NAME(l.resource_database_id) AS [Database], l.resource_type AS [Resource],
                   CASE WHEN l.resource_type = 'OBJECT'
                        THEN OBJECT_SCHEMA_NAME(l.resource_associated_entity_id, l.resource_database_id) + N'.' + OBJECT_NAME(l.resource_associated_entity_id, l.resource_database_id)
                   END AS [Object],
                   l.request_mode AS [Mode], l.request_status AS [Status], COUNT(*) AS [Locks]
            FROM sys.dm_tran_locks l
            WHERE l.resource_type <> 'DATABASE' AND l.request_session_id IN ({gatewaySessions})
            GROUP BY l.request_session_id, l.resource_database_id, l.resource_type, l.resource_associated_entity_id, l.request_mode, l.request_status
            ORDER BY l.request_session_id, COUNT(*) DESC
            """,
            cancellationToken,
            "Objects are named for table locks; key, page and row locks show their type only.");

        var victims = await SectionAsync(
            connection,
            "Sessions they block",
            "sys.dm_exec_requests",
            $"""
            SELECT r.blocking_session_id AS [Blocked by], r.session_id AS [Session], s.login_name AS [Login], s.host_name AS [Host],
                   s.program_name AS [Program], DB_NAME(r.database_id) AS [Database], r.wait_type AS [Wait],
                   r.wait_time / 1000 AS [Waiting s], r.wait_resource AS [Resource],
                   CAST(LEFT(t.text, 4000) AS nvarchar(4000)) AS [SQL]
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.blocking_session_id IN ({gatewaySessions}) AND r.blocking_session_id <> r.session_id
            ORDER BY r.wait_time DESC
            """,
            cancellationToken);

        return new ToolReport(warnings, [], [sessions, locks, victims], now);
    }

    // ---- Shared ----

    private static async Task<ToolSection> DtcSectionAsync(SqlConnection connection, List<string> warnings, CancellationToken cancellationToken)
    {
        var dtc = await SectionAsync(
            connection,
            "Distributed (MSDTC) transactions",
            "sys.dm_tran_active_transactions, sys.dm_tran_locks (session -2)",
            DtcSql,
            cancellationToken,
            "An orphaned transaction has no session: its locks belong to session -2, which the Blocking Manager shows as the blocker. "
            + "KILL takes it by its UOW. A Prepared one is in doubt: find what the other side decided "
            + "(on Oracle, the Database Links report's in-doubt transactions) and commit or roll back to match.");

        var scripts = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(dtc))
        {
            var uow = ToolReports.Cell(columns, row, "UOW");
            var state = ToolReports.Cell(columns, row, "DTC state");
            var orphaned = string.Equals(ToolReports.Cell(columns, row, "Orphaned"), "Yes", StringComparison.OrdinalIgnoreCase);
            var locks = ToolReports.Number(columns, row, "Orphaned locks") ?? 0;
            if (!orphaned || uow is null || !Guid.TryParse(uow, out var guid) || guid == Guid.Empty)
            {
                continue;
            }

            warnings.Add($"Distributed transaction {uow} ({state}) has no session{(locks > 0 ? $" and holds {locks:0} locks as session -2" : string.Empty)}.");
            if (scripts.Count < MaxScripts)
            {
                scripts.AddRange(UowScripts(guid, state));
            }
        }

        return dtc with { Scripts = scripts };
    }

    /// <summary>An orphaned UOW is killed by its GUID; an in-doubt (prepared) one must be told which way to go.</summary>
    internal static IReadOnlyList<ToolScript> UowScripts(Guid uow, string? dtcState)
    {
        var id = uow.ToString("D").ToUpperInvariant();
        return string.Equals(dtcState, "Prepared", StringComparison.OrdinalIgnoreCase)
            ? [
                new ToolScript($"Roll back {id}", $"-- In doubt: check the other side decided to roll back first.\nKILL '{id}' WITH ROLLBACK;"),
                new ToolScript($"Commit {id}", $"-- In doubt: check the other side committed first.\nKILL '{id}' WITH COMMIT;"),
            ]
            : [new ToolScript($"Kill {id}", $"KILL '{id}';")];
    }

    /// <summary>
    /// A warning for each session sleeping with a transaction open that's been idle a while or is blocking someone;
    /// those that block come first.
    /// </summary>
    private static IEnumerable<string> IdleWarnings(ToolSection section, string what)
    {
        var found = new List<(double Blocking, double Idle, string Text)>();
        foreach (var (columns, row) in ToolReports.Rows(section))
        {
            if ((ToolReports.Number(columns, row, "Open trans") ?? 0) == 0 || ToolReports.Cell(columns, row, "Status") != "sleeping")
            {
                continue;
            }

            var idle = ToolReports.Number(columns, row, "Idle s") ?? 0;
            var blocking = ToolReports.Number(columns, row, "Blocking") ?? 0;
            var locks = ToolReports.Number(columns, row, "Locks") ?? 0;
            if (blocking == 0 && idle < IdleWarningSeconds)
            {
                continue;
            }

            var text = $"{what} {ToolReports.Cell(columns, row, "Session")} ({Who(columns, row)}) has slept {Duration(idle)} with a transaction open"
                + (ToolReports.Number(columns, row, "Transaction s") is { } age ? $" for {Duration(age)}" : string.Empty)
                + $", holding {locks:0} {(locks == 1 ? "lock" : "locks")}"
                + (blocking > 0 ? $" and blocking {blocking:0} {(blocking == 1 ? "session" : "sessions")}." : ".");
            found.Add((blocking, idle, text));
        }

        var ordered = found.OrderByDescending(f => f.Blocking > 0).ThenByDescending(f => f.Idle).ToList();
        foreach (var (_, _, text) in ordered.Take(MaxWarningsPerKind))
        {
            yield return text;
        }

        if (ordered.Count > MaxWarningsPerKind)
        {
            yield return $"…and {ordered.Count - MaxWarningsPerKind} more {what.ToLowerInvariant()}s sleeping with a transaction open.";
        }
    }

    /// <summary>
    /// KILL scripts for a section's sessions: every one (<paramref name="all"/>), those sleeping with a transaction
    /// (<paramref name="idleWithTransaction"/>), or else those blocking others. Those that block come first.
    /// </summary>
    private static IReadOnlyList<ToolScript> KillScripts(ToolSection section, bool all, bool idleWithTransaction = false)
    {
        var candidates = new List<(double Blocking, ToolScript Script)>();
        foreach (var (columns, row) in ToolReports.Rows(section))
        {
            if (!int.TryParse(ToolReports.Cell(columns, row, "Session"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                continue;
            }

            var blocking = ToolReports.Number(columns, row, "Blocking") ?? 0;
            var idleTransaction = ToolReports.Cell(columns, row, "Status") == "sleeping" && (ToolReports.Number(columns, row, "Open trans") ?? 0) > 0;
            if (all || blocking > 0 || (idleWithTransaction && idleTransaction))
            {
                candidates.Add((blocking, KillScript(id, Who(columns, row), blocking, ToolReports.Number(columns, row, "Open trans") ?? 0)));
            }
        }

        return [.. candidates.OrderByDescending(c => c.Blocking).Take(MaxScripts).Select(c => c.Script)];
    }

    internal static ToolScript KillScript(int sessionId, string who, double blocking, double openTransactions)
    {
        var id = sessionId.ToString(CultureInfo.InvariantCulture);
        var label = blocking > 0 ? $"Kill {id} (blocking {blocking:0})" : $"Kill {id}";
        var comment = $"-- Session {id}: {who}." + (openTransactions > 0 ? " Its open transaction is rolled back." : string.Empty);
        return new ToolScript(label, $"{comment}\nKILL {id};");
    }

    private static string Who(IReadOnlyList<SqlColumn> columns, string?[] row)
    {
        var login = ToolReports.Cell(columns, row, "Login");
        var host = ToolReports.Cell(columns, row, "Host");
        var program = ToolReports.Cell(columns, row, "Program");
        var who = string.Join("@", new[] { login, host }.Where(p => !string.IsNullOrEmpty(p)));
        return string.IsNullOrEmpty(program) ? who : $"{who}, {program}";
    }

    internal static string Duration(double seconds) => seconds switch
    {
        < 120 => $"{seconds:0} s",
        < 7200 => $"{seconds / 60:0} min",
        < 172_800 => $"{seconds / 3600:0.#} h",
        _ => $"{seconds / 86_400:0.#} days",
    };

    private static string Literal(string text) => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>Runs a query and keeps its result; a failure (e.g. without VIEW SERVER STATE) is the section's error, not the report's.</summary>
    private static async Task<ToolSection> SectionAsync(
        SqlConnection connection, string title, string source, string sql, CancellationToken cancellationToken, string? note = null)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new ToolSection(title, source, [await SqlQueryRunner.ReadResultAsync(reader, RowsPerResult, cancellationToken)], null, null, note);
        }
        catch (SqlException ex)
        {
            return new ToolSection(title, source, [], ex.Message, null, note);
        }
    }
}
