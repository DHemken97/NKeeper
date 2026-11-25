using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Oracle;

/// <summary>
/// Oracle's Database Links report: the links, the Heterogeneous Services agents (Database Gateway processes:
/// DG4MSQL, DG4ODBC…) and the sessions using them, sessions idle with a distributed transaction open, sessions
/// waiting on a link now, and in-doubt distributed transactions (DBA_2PC_PENDING, DBA_2PC_NEIGHBORS).
/// It reads V$ and DBA_ views, so the login needs SELECT_CATALOG_ROLE; a section it can't read shows why.
/// Kill, FORCE and purge are offered as scripts, never run.
/// </summary>
internal static class OracleLinks
{
    public const string Tool = "dblinks";

    private const int MaxScripts = 20;
    private const int MaxWarningsPerKind = 8;
    private const int IdleWarningSeconds = 60;

    public static SqlObjectNode ToolNode =>
        new() { Kind = Kinds.Tool, Label = "Database Links", Detail = "Links, gateways, in-doubt transactions", Name = Tool };

    public static async Task<ToolReport> ReportAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var links = await OracleDba.SectionAsync(
            connection,
            "Database links",
            "dba_db_links",
            """
            SELECT owner "Owner", db_link "Link", username "Connects as", host "Host", TO_CHAR(created, 'YYYY-MM-DD') "Created",
                   CASE WHEN UPPER(host) LIKE '%HS=OK%' THEN 'Yes' END "Gateway"
            FROM dba_db_links ORDER BY owner, db_link
            """,
            cancellationToken,
            fallbackSql:
            """
            SELECT owner "Owner", db_link "Link", username "Connects as", host "Host", TO_CHAR(created, 'YYYY-MM-DD') "Created",
                   CASE WHEN UPPER(host) LIKE '%HS=OK%' THEN 'Yes' END "Gateway"
            FROM all_db_links ORDER BY owner, db_link
            """,
            note: "A link through a gateway has (HS=OK) in its connect descriptor; one using a TNS alias has it in tnsnames.ora instead.");

        var agents = await OracleDba.SectionAsync(
            connection,
            "Gateway agents",
            "v$hs_agent",
            """
            SELECT a.agent_id "Agent", a.machine "Machine", a.process "Process", a.program "Program", a.osuser "OS user",
                   TO_CHAR(a.starttime, 'YYYY-MM-DD HH24:MI:SS') "Started", ROUND((SYSDATE - a.starttime) * 86400) "Age s",
                   a.agent_type "Type", a.fds_class_id "FDS class", a.fds_inst_id "FDS instance",
                   (SELECT COUNT(*) FROM v$hs_session h WHERE h.agent_id = a.agent_id) "Sessions"
            FROM v$hs_agent a ORDER BY a.starttime
            """,
            cancellationToken,
            note: "Each is a gateway process with a connection of its own to the remote database. "
                + "“Process” is that process's ID: on SQL Server it's the session's Client PID in the Oracle Gateway report.");

        var gatewaySessions = await OracleDba.SectionAsync(
            connection,
            "Sessions using a gateway",
            "v$hs_session, v$session",
            """
            SELECT h.hs_session_id "HS session", h.agent_id "Agent", h.db_link "Link", h.db_link_owner "Link owner",
                   s.sid "SID", s.serial# "Serial", s.username "User", s.osuser "OS user", s.machine "Machine", s.program "Program",
                   s.status "Status", CASE WHEN s.status = 'INACTIVE' THEN s.last_call_et END "Idle s",
                   CASE WHEN s.taddr IS NOT NULL THEN 'Yes' ELSE 'No' END "Open transaction",
                   (SELECT COUNT(*) FROM v$session b WHERE b.blocking_session = s.sid) "Blocking",
                   s.event "Event", TO_CHAR(h.starttime, 'YYYY-MM-DD HH24:MI:SS') "Started"
            FROM v$hs_session h LEFT JOIN v$session s ON s.sid = h.sid
            ORDER BY CASE WHEN s.taddr IS NOT NULL THEN 0 ELSE 1 END, s.last_call_et DESC NULLS LAST
            """,
            cancellationToken,
            note: "Oracle starts a distributed transaction for any statement through a link, even a SELECT: until the session commits or rolls back, "
                + "the gateway keeps the remote transaction (and its locks) open. A session with no SID left is an agent whose session has gone.");
        foreach (var (columns, row) in ToolReports.Rows(gatewaySessions))
        {
            if (ToolReports.Cell(columns, row, "SID") is null)
            {
                warnings.Add($"Gateway agent {ToolReports.Cell(columns, row, "Agent")} (link {ToolReports.Cell(columns, row, "Link")}) still has a session but no Oracle session: an orphaned agent.");
            }
        }

        var idle = await OracleDba.SectionAsync(
            connection,
            "Idle with a transaction open",
            "v$session, v$transaction",
            """
            SELECT s.sid "SID", s.serial# "Serial", s.username "User", s.osuser "OS user", s.machine "Machine", s.program "Program",
                   s.status "Status", s.last_call_et "Idle s",
                   TO_CHAR(t.start_date, 'YYYY-MM-DD HH24:MI:SS') "Transaction began", ROUND((SYSDATE - t.start_date) * 86400) "Transaction s",
                   t.used_ublk "Undo blocks",
                   CASE WHEN EXISTS (SELECT 1 FROM v$hs_session h WHERE h.sid = s.sid) THEN 'Yes' ELSE 'No' END "Through gateway",
                   (SELECT COUNT(*) FROM v$session b WHERE b.blocking_session = s.sid) "Blocking"
            FROM v$session s JOIN v$transaction t ON t.ses_addr = s.saddr
            WHERE s.status = 'INACTIVE' AND s.type = 'USER'
            ORDER BY s.last_call_et DESC
            """,
            cancellationToken,
            fallbackSql:
            """
            SELECT s.sid "SID", s.serial# "Serial", s.username "User", s.osuser "OS user", s.machine "Machine", s.program "Program",
                   s.status "Status", s.last_call_et "Idle s",
                   TO_CHAR(t.start_date, 'YYYY-MM-DD HH24:MI:SS') "Transaction began", ROUND((SYSDATE - t.start_date) * 86400) "Transaction s",
                   t.used_ublk "Undo blocks",
                   (SELECT COUNT(*) FROM v$session b WHERE b.blocking_session = s.sid) "Blocking"
            FROM v$session s JOIN v$transaction t ON t.ses_addr = s.saddr
            WHERE s.status = 'INACTIVE' AND s.type = 'USER'
            ORDER BY s.last_call_et DESC
            """,
            note: "Killing the Oracle session rolls back its transaction here, and its gateway agent ends its remote transaction with it.");
        var found = new List<(bool Gateway, double Blocking, double Idle, string Text)>();
        var kills = new List<(double Rank, ToolScript Script)>();
        foreach (var (columns, row) in ToolReports.Rows(idle))
        {
            var sid = ToolReports.Cell(columns, row, "SID");
            var serial = ToolReports.Cell(columns, row, "Serial");
            var seconds = ToolReports.Number(columns, row, "Idle s") ?? 0;
            var blocking = ToolReports.Number(columns, row, "Blocking") ?? 0;
            var gateway = ToolReports.Cell(columns, row, "Through gateway") == "Yes";
            var who = Who(columns, row);
            if (blocking > 0 || (gateway && seconds >= IdleWarningSeconds))
            {
                found.Add((gateway, blocking, seconds,
                    $"Session {sid},{serial} ({who}) has been idle {SqlSessionTools.Duration(seconds)} with a transaction open"
                    + (gateway ? " through a gateway" : string.Empty)
                    + (blocking > 0 ? $", blocking {blocking:0} {(blocking == 1 ? "session" : "sessions")}." : ".")));
            }

            if (sid is not null && serial is not null && (gateway || blocking > 0))
            {
                kills.Add((blocking * 2 + (gateway ? 1 : 0), KillScript(sid, serial, who, blocking)));
            }
        }

        var ordered = found.OrderByDescending(f => f.Blocking > 0).ThenByDescending(f => f.Gateway).ThenByDescending(f => f.Idle).ToList();
        warnings.AddRange(ordered.Take(MaxWarningsPerKind).Select(f => f.Text));
        if (ordered.Count > MaxWarningsPerKind)
        {
            warnings.Add($"…and {ordered.Count - MaxWarningsPerKind} more sessions idle with a transaction open.");
        }

        idle = idle with { Scripts = [.. kills.OrderByDescending(k => k.Rank).Take(MaxScripts).Select(k => k.Script)] };

        var waiting = await OracleDba.SectionAsync(
            connection,
            "Waiting on a link now",
            "v$session (dblink waits)",
            """
            SELECT s.sid "SID", s.serial# "Serial", s.username "User", s.machine "Machine", s.program "Program", s.status "Status",
                   s.event "Event", s.seconds_in_wait "Waiting s", s.sql_id "SQL ID", s.blocking_session "Blocked by",
                   (SELECT COUNT(*) FROM v$session b WHERE b.blocking_session = s.sid) "Blocking"
            FROM v$session s
            WHERE s.event LIKE '%dblink%' AND s.state = 'WAITING' AND s.type = 'USER'
            ORDER BY s.seconds_in_wait DESC
            """,
            cancellationToken,
            note: "“SQL*Net message from dblink” on an ACTIVE session is the remote database (or gateway) working, or not answering.");
        var waitKills = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(waiting))
        {
            var blocking = ToolReports.Number(columns, row, "Blocking") ?? 0;
            var seconds = ToolReports.Number(columns, row, "Waiting s") ?? 0;
            var active = ToolReports.Cell(columns, row, "Status") == "ACTIVE";
            if (active && (blocking > 0 || seconds >= 300))
            {
                warnings.Add($"Session {ToolReports.Cell(columns, row, "SID")} has waited {SqlSessionTools.Duration(seconds)} on {ToolReports.Cell(columns, row, "Event")}"
                    + (blocking > 0 ? $", blocking {blocking:0}." : "."));
                if (waitKills.Count < MaxScripts && ToolReports.Cell(columns, row, "SID") is { } sid && ToolReports.Cell(columns, row, "Serial") is { } serial)
                {
                    waitKills.Add(KillScript(sid, serial, Who(columns, row), blocking));
                }
            }
        }

        waiting = waiting with { Scripts = waitKills };

        var pending = await OracleDba.SectionAsync(
            connection,
            "In-doubt distributed transactions",
            "dba_2pc_pending",
            """
            SELECT local_tran_id "Local transaction", global_tran_id "Global transaction", state "State", mixed "Mixed", advice "Advice",
                   tran_comment "Comment", TO_CHAR(fail_time, 'YYYY-MM-DD HH24:MI:SS') "Failed", TO_CHAR(retry_time, 'YYYY-MM-DD HH24:MI:SS') "Retried",
                   os_user "OS user", host "Host", db_user "DB user", commit# "Commit#"
            FROM dba_2pc_pending ORDER BY fail_time
            """,
            cancellationToken,
            note: "RECO resolves these by itself once every database in the transaction can be reached. One stuck “prepared” holds its locks "
                + "(ORA-01591) until it's forced: commit if any other site committed (see the neighbors below and the remote's own record), roll back otherwise. "
                + "Forcing needs FORCE ANY TRANSACTION; purging an entry needs SYSDBA or DBA.");
        var resolve = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(pending))
        {
            var id = ToolReports.Cell(columns, row, "Local transaction");
            var state = ToolReports.Cell(columns, row, "State");
            if (id is null)
            {
                continue;
            }

            warnings.Add($"Distributed transaction {id} is in doubt ({state}{(ToolReports.Cell(columns, row, "Failed") is { } at ? $" since {at}" : string.Empty)}).");
            if (resolve.Count < MaxScripts)
            {
                resolve.AddRange(InDoubtScripts(id, state));
            }
        }

        pending = pending with { Scripts = resolve };

        var neighbors = await OracleDba.SectionAsync(
            connection,
            "Where they went",
            "dba_2pc_neighbors",
            """
            SELECT local_tran_id "Local transaction", in_out "Direction", database "Database / link", dbuser_owner "User",
                   interface "Interface", dbid "Remote DB id", sess# "Session", branch "Branch"
            FROM dba_2pc_neighbors ORDER BY local_tran_id, in_out
            """,
            cancellationToken,
            note: "OUT is a link this database called; IN is a database that called this one. Interface N is a gateway (a non-Oracle database).");

        return new ToolReport(warnings, [], [links, agents, gatewaySessions, idle, waiting, pending, neighbors], now);
    }

    /// <summary>What to run for an in-doubt transaction, by its state: force a prepared one either way; purge one that's decided.</summary>
    internal static IReadOnlyList<ToolScript> InDoubtScripts(string localTransactionId, string? state)
    {
        var id = OracleDba.Literal(localTransactionId);
        var purge = new ToolScript(
            $"Purge {localTransactionId}",
            $"BEGIN\n  DBMS_TRANSACTION.PURGE_LOST_DB_ENTRY({id});\n  COMMIT;\nEND;\n/");
        return (state ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "prepared" or "collecting" =>
            [
                new ToolScript($"Roll back {localTransactionId}", $"-- Only if no other site committed it.\nROLLBACK FORCE {id};"),
                new ToolScript($"Commit {localTransactionId}", $"-- Only if another site committed it.\nCOMMIT FORCE {id};"),
                purge,
            ],
            _ => [purge],
        };
    }

    internal static ToolScript KillScript(string sid, string serial, string who, double blocking)
    {
        var label = blocking > 0 ? $"Kill {sid},{serial} (blocking {blocking:0})" : $"Kill {sid},{serial}";
        return new ToolScript(
            label,
            $"-- Session {sid},{serial}: {who}. Its open transaction is rolled back, here and through its links.\nALTER SYSTEM KILL SESSION '{Number(sid)},{Number(serial)}' IMMEDIATE;");
    }

    private static string Number(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : throw new FormatException($"“{text}” isn't a session number.");

    private static string Who(IReadOnlyList<SqlColumn> columns, string?[] row)
    {
        var user = ToolReports.Cell(columns, row, "User");
        var machine = ToolReports.Cell(columns, row, "Machine");
        var program = ToolReports.Cell(columns, row, "Program");
        var who = string.Join("@", new[] { user, machine }.Where(p => !string.IsNullOrEmpty(p)));
        return string.IsNullOrEmpty(program) ? who : $"{who}, {program}";
    }
}
