using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>
/// SAP ASE's daily-check reports: Backups (dumps), Error Log, Resources &amp; Activity, Logins &amp; Users and
/// Maintenance. They read system tables and the monitoring (MDA) tables; the latter need mon_role and
/// 'enable monitoring', and a section that can't read them says so and offers the settings to turn on.
/// </summary>
internal static class AseHealth
{
    public const string Dumps = "dumps";
    public const string Errors = "errors";
    public const string Resources = "resources";
    public const string Security = "security";
    public const string Maintenance = "maintenance";

    private const int MaxScripts = 20;

    /// <summary>Databases a DBA doesn't dump or maintain: the server's own, and tempdb.</summary>
    private static readonly HashSet<string> NotDumped = new(StringComparer.OrdinalIgnoreCase)
    {
        "tempdb", "model", "sybsystemprocs", "sybsystemdb", "sybpcidb", "dbccdb", "dbccalt", "sybmgmtdb", "sybsecurity",
    };

    public static IReadOnlyList<SqlObjectNode> ToolNodes =>
    [
        new() { Kind = Kinds.Tool, Label = "Backups (dumps)", Detail = "Last full and log dumps", Name = Dumps },
        new() { Kind = Kinds.Tool, Label = "Error Log", Detail = "Recent errors and deadlocks", Name = Errors },
        new() { Kind = Kinds.Tool, Label = "Resources & Activity", Detail = "Limits, sessions, caches", Name = Resources },
        new() { Kind = Kinds.Tool, Label = "Logins & Users", Detail = "Locked logins, users without logins", Name = Security },
        new() { Kind = Kinds.Tool, Label = "Maintenance", Detail = "Big tables, stale statistics", Name = Maintenance },
    ];

    public static Task<ToolReport> ReportAsync(DbConnection connection, string tool, DateTimeOffset now, CancellationToken cancellationToken) => tool switch
    {
        Dumps => DumpsAsync(connection, now, cancellationToken),
        Errors => ErrorsAsync(connection, now, cancellationToken),
        Resources => ResourcesAsync(connection, now, cancellationToken),
        Security => SecurityAsync(connection, now, cancellationToken),
        Maintenance => MaintenanceAsync(connection, now, cancellationToken),
        _ => throw new SqlExplorerException($"There's no “{tool}” report."),
    };

    /// <summary>Settings that are meant to be in full use (every engine online, all memory given out): not a shortage.</summary>
    private static readonly HashSet<string> FullByDesign = new(StringComparer.OrdinalIgnoreCase)
    {
        "max online engines", "number of engines at startup", "max memory", "total logical memory", "max online Q engines",
    };

    private const string MonitoringNote = "Monitoring tables need mon_role and 'enable monitoring' (see the script).";

    private static readonly ToolScript EnableMonitoring = new(
        "Turn on monitoring",
        "exec sp_configure 'enable monitoring', 1");

    // ---- Backups (dumps) ----

    private static async Task<ToolReport> DumpsAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var boot = await ScalarDateAsync(connection, "SELECT @@boottime", cancellationToken);

        var dumps = await SectionAsync(
            connection,
            "Last dumps",
            "master..sysdatabases, master..monOpenDatabases",
            $"""
            SELECT d.name "Database",
                   {Stamp("o.BackupStartTime")} "Last full dump started",
                   datediff(hh, o.BackupStartTime, getdate()) "Hours since full",
                   {Stamp("isnull(o.LastTranLogDumpTime, d.dumptrdate)")} "Last log dump",
                   datediff(hh, isnull(o.LastTranLogDumpTime, d.dumptrdate), getdate()) "Hours since log dump",
                   CASE WHEN d.status & 8 = 8 THEN 'yes' ELSE 'no' END "Trunc log on chkpt",
                   CASE WHEN o.BackupInProgress > 0 THEN 'yes' END "Dumping now",
                   CASE WHEN o.LastBackupFailed > 0 THEN 'yes' END "Last dump failed",
                   CASE WHEN o.TransactionLogFull > 0 THEN 'yes' END "Log full",
                   o.SuspendedProcesses "Suspended processes"
            FROM master..sysdatabases d LEFT JOIN master..monOpenDatabases o ON o.DBID = d.dbid
            WHERE d.status2 & 16 = 0 AND d.status & 32 = 0
            ORDER BY d.name
            """,
            cancellationToken,
            fallbackSql: $"""
            SELECT d.name "Database", {Stamp("d.dumptrdate")} "Last log dump",
                   datediff(hh, d.dumptrdate, getdate()) "Hours since log dump",
                   CASE WHEN d.status & 8 = 8 THEN 'yes' ELSE 'no' END "Trunc log on chkpt"
            FROM master..sysdatabases d
            WHERE d.status2 & 16 = 0 AND d.status & 32 = 0
            ORDER BY d.name
            """,
            note: $"Full dump times come from monitoring, so they're known since the server started{(boot is { } b ? $" ({b:yyyy-MM-dd HH:mm})" : "")}; log dump times are kept by the server. " +
                  "Turn on dump history to keep every dump's time.");

        var scripts = new List<ToolScript>();
        var monitored = (dumps.Results.Count > 0 ? dumps.Results[0].Columns : null)?.Any(c => c.Name == "Hours since full") == true;
        var uptimeHours = boot is { } started ? (now.LocalDateTime - started).TotalHours : (double?)null;
        foreach (var (columns, row) in ToolReports.Rows(dumps))
        {
            var db = ToolReports.Cell(columns, row, "Database") ?? "?";
            if (ToolReports.Cell(columns, row, "Log full") == "yes")
            {
                var suspended = ToolReports.Number(columns, row, "Suspended processes") ?? 0;
                warnings.Insert(0, $"{db}: the transaction log is full{(suspended > 0 ? $"; {suspended:0} process{(suspended == 1 ? " is" : "es are")} suspended waiting for it" : "")}. Dump the log, or add log space.");
            }

            if (ToolReports.Cell(columns, row, "Last dump failed") == "yes")
            {
                warnings.Add($"{db}: its last dump failed (started {ToolReports.Cell(columns, row, "Last full dump started") ?? "at an unknown time"}).");
            }

            if (NotDumped.Contains(db))
            {
                continue;
            }

            var dueFull = false;
            if (monitored)
            {
                var full = ToolReports.Number(columns, row, "Hours since full");
                if (full > 26)
                {
                    dueFull = true;
                    warnings.Add($"{db}: no full dump for {full:0} hours (the last started {ToolReports.Cell(columns, row, "Last full dump started")}).");
                }
                else if (full is null && uptimeHours > 26)
                {
                    dueFull = true;
                    warnings.Add($"{db}: no full dump since the server started ({boot:yyyy-MM-dd HH:mm}).");
                }
            }

            var dueLog = false;
            if (ToolReports.Cell(columns, row, "Trunc log on chkpt") == "no" && !db.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                var log = ToolReports.Number(columns, row, "Hours since log dump");
                if (log is null)
                {
                    dueLog = true;
                    warnings.Add($"{db}: its log has never been dumped, and 'trunc log on chkpt' is off, so the log only grows. Dump it on a schedule, or turn that option on if you don't need point-in-time recovery.");
                }
                else if (log > 26)
                {
                    dueLog = true;
                    warnings.Add($"{db}: its log was last dumped {log:0} hours ago ({ToolReports.Cell(columns, row, "Last log dump")}).");
                }
            }

            if (dueFull && scripts.Count < MaxScripts)
            {
                scripts.Add(new ToolScript($"Dump {db}", $"dump database {AseDba.Quote(db)} to '/path/to/{db}.dmp' with compression = 101"));
            }

            if (dueLog && scripts.Count < MaxScripts)
            {
                scripts.Add(new ToolScript($"Dump {db}'s log", $"dump transaction {AseDba.Quote(db)} to '/path/to/{db}_log.dmp'"));
            }
        }

        scripts.Add(new ToolScript("Keep every dump's time (dump history)", "exec sp_configure 'enable dump history', 1"));
        scripts.Add(new ToolScript("A database's log thresholds", "exec sp_helpthreshold"));
        if (!monitored)
        {
            scripts.Add(EnableMonitoring);
        }

        dumps = dumps with { Scripts = scripts };

        var history = await ScalarIntAsync(connection, "SELECT value FROM master..sysconfigures WHERE name = 'enable dump history'", cancellationToken) == 1
            ? await AseReplication.SectionAsync(connection, "Dump history", "sp_dump_history", cancellationToken)
            : null;

        return new ToolReport(warnings, [], history is null ? [dumps] : [dumps, history], now);
    }

    // ---- Error log ----

    private static async Task<ToolReport> ErrorsAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = await SectionAsync(
            connection,
            "Errors (24 hours)",
            "master..monErrorLog",
            $"""
            SELECT TOP 200 {Stamp("Time")} "Time", Severity "Severity", ErrorNumber "Error", SPID "SPID", ErrorMessage "Message"
            FROM master..monErrorLog
            WHERE Time > dateadd(hh, -24, getdate())
            ORDER BY Time DESC
            """,
            cancellationToken,
            note: "The error log as the server keeps it in memory (its 'errorlog pipe max messages' most recent messages); the file on the server has everything. " + MonitoringNote);
        var serious = ToolReports.Rows(errors).Where(x => ToolReports.Number(x.Columns, x.Row, "Severity") >= 17).ToList();
        if (serious.Count > 0)
        {
            var (columns, row) = serious[0];
            warnings.Add($"{serious.Count} serious error{(serious.Count == 1 ? "" : "s")} (severity 17 or more) in the last 24 hours; the latest at {ToolReports.Cell(columns, row, "Time")}: {Shorten(ToolReports.Cell(columns, row, "Message"))}");
        }

        errors = errors with
        {
            Scripts =
            [
                new ToolScript(
                    "Keep recent errors in memory",
                    "exec sp_configure 'enable monitoring', 1\nexec sp_configure 'errorlog pipe max messages', 1000\nexec sp_configure 'errorlog pipe active', 1"),
            ],
        };

        var deadlocks = await SectionAsync(
            connection,
            "Deadlocks (24 hours)",
            "master..monDeadLock",
            $"""
            SELECT TOP 100 {Stamp("ResolveTime")} "Resolved", ObjectDBName "Database", ObjectName "Object",
                   HeldSPID "Held by SPID", HeldUserName "Held by", HeldCommand "Held command",
                   WaitSPID "Waiting SPID", WaitUserName "Waiting user", WaitCommand "Waiting command"
            FROM master..monDeadLock
            WHERE ResolveTime > dateadd(hh, -24, getdate())
            ORDER BY ResolveTime DESC
            """,
            cancellationToken,
            note: MonitoringNote);
        var deadlockRows = ToolReports.Rows(deadlocks).ToList();
        if (deadlockRows.Count > 0)
        {
            var (columns, row) = deadlockRows[0];
            warnings.Add($"{deadlockRows.Count} deadlock{(deadlockRows.Count == 1 ? "" : "s")} in the last 24 hours; the latest at {ToolReports.Cell(columns, row, "Resolved")} on {ToolReports.Cell(columns, row, "Database")}.{ToolReports.Cell(columns, row, "Object")}.");
        }

        deadlocks = deadlocks with
        {
            Scripts =
            [
                new ToolScript(
                    "Keep recent deadlocks in memory",
                    "exec sp_configure 'enable monitoring', 1\nexec sp_configure 'deadlock pipe max messages', 100\nexec sp_configure 'deadlock pipe active', 1"),
                new ToolScript("Write deadlock details to the error log", "exec sp_configure 'print deadlock information', 1"),
            ],
        };

        return new ToolReport(warnings, [], [errors, deadlocks], now);
    }

    // ---- Resources & activity ----

    private static async Task<ToolReport> ResourcesAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var limits = await AseReplication.SectionAsync(connection, "Configured limits in use", "sp_monitorconfig 'all'", cancellationToken);
        var raise = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(limits))
        {
            var name = ToolReports.Cell(columns, row, "Name");
            var percent = ToolReports.Number(columns, row, "Pct_act");
            var free = ToolReports.Number(columns, row, "Num_free") ?? 0;
            var active = ToolReports.Number(columns, row, "Num_active") ?? 0;
            var maxUsed = ToolReports.Number(columns, row, "Max_Used") ?? 0;
            var total = free + active;
            var highest = total > 0 ? 100 * maxUsed / total : 0;
            if (name is null || FullByDesign.Contains(name) || (percent < 85 && highest < 95))
            {
                continue;
            }

            warnings.Add($"{name} is {Math.Max(percent ?? 0, 0):0}% used now (at most {highest:0}% since the server started): when it runs out, work fails or waits.");
            if (raise.Count < MaxScripts && total > 0)
            {
                raise.Add(new ToolScript($"Raise {name}", $"exec sp_configure '{name}', {Math.Ceiling(total * 1.25).ToString("0", CultureInfo.InvariantCulture)}"));
            }
        }

        limits = limits with
        {
            Scripts = raise,
            Note = "Since the server started: Max_Used is the most ever in use at once; Reuse_cnt counts times something had to be thrown out for room.",
        };

        var sessions = await SectionAsync(
            connection,
            "Top sessions by CPU",
            "master..sysprocesses",
            """
            SELECT TOP 20 spid "SPID", suser_name(suid) "Login", program_name "Program", hostname "Host", db_name(dbid) "Database",
                   cmd "Command", status "Status", cpu "CPU", physical_io "Physical IO", memusage "Memory", blocked "Blocked by"
            FROM master..sysprocesses
            WHERE spid <> @@spid AND suid > 0
            ORDER BY cpu DESC
            """,
            cancellationToken);

        var transactions = await SectionAsync(
            connection,
            "Open transactions",
            "master..syslogshold, master..sysprocesses",
            $"""
            SELECT db_name(h.dbid) "Database", h.spid "SPID", suser_name(p.suid) "Login", p.program_name "Program",
                   {Stamp("h.starttime")} "Open since", datediff(mi, h.starttime, getdate()) "Minutes", h.name "Transaction"
            FROM master..syslogshold h LEFT JOIN master..sysprocesses p ON p.spid = h.spid
            WHERE h.spid <> 0
            ORDER BY h.starttime
            """,
            cancellationToken,
            note: "The oldest open transaction in each database: its log can't be truncated past it.");
        foreach (var (columns, row) in ToolReports.Rows(transactions))
        {
            if (ToolReports.Number(columns, row, "Minutes") >= 60)
            {
                warnings.Add($"SPID {ToolReports.Cell(columns, row, "SPID")} ({ToolReports.Cell(columns, row, "Login")}, {ToolReports.Cell(columns, row, "Program")}) has had a transaction open in {ToolReports.Cell(columns, row, "Database")} since {ToolReports.Cell(columns, row, "Open since")}: the log can't be truncated past it.");
            }
        }

        var caches = await SectionAsync(
            connection,
            "Data caches",
            "master..monDataCache",
            """
            SELECT CacheName "Cache", CacheSize "Size KB", LogicalReads "Logical reads", PhysicalReads "Physical reads",
                   CASE WHEN LogicalReads > 0 THEN convert(numeric(5, 1), 100.0 * (LogicalReads - PhysicalReads) / LogicalReads) END "Hit %",
                   Stalls "Stalls"
            FROM master..monDataCache
            ORDER BY CacheName
            """,
            cancellationToken,
            note: "Since the server started. " + MonitoringNote);
        foreach (var (columns, row) in ToolReports.Rows(caches))
        {
            if (ToolReports.Number(columns, row, "Hit %") < 90 && ToolReports.Number(columns, row, "Logical reads") > 100_000)
            {
                warnings.Add($"Cache {ToolReports.Cell(columns, row, "Cache")} finds only {ToolReports.Cell(columns, row, "Hit %")}% of pages in memory: it may be too small.");
            }
        }

        var engines = await SectionAsync(
            connection,
            "Engines",
            "master..monEngine",
            """
            SELECT EngineNumber "Engine", ThreadID "Thread", Connections "Connections", CPUTime "CPU ticks", UserCPUTime "User", SystemCPUTime "System",
                   IOCPUTime "IO", IdleCPUTime "Idle",
                   CASE WHEN CPUTime > 0 THEN convert(numeric(5, 1), 100.0 * (CPUTime - IdleCPUTime) / CPUTime) END "Busy %"
            FROM master..monEngine
            ORDER BY EngineNumber
            """,
            cancellationToken,
            note: "Since the server started. " + MonitoringNote);
        if (caches.Error is not null || engines.Error is not null)
        {
            engines = engines with { Scripts = [EnableMonitoring] };
        }

        return new ToolReport(warnings, [], [limits, transactions, sessions, caches, engines], now);
    }

    // ---- Logins & users ----

    private static async Task<ToolReport> SecurityAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var logins = await SectionAsync(
            connection,
            "Locked or expired logins",
            "master..syslogins",
            $"""
            SELECT name "Login",
                   CASE WHEN status & 2 = 2 THEN 'yes' END "Locked",
                   CASE WHEN status & 4 = 4 THEN 'yes' END "Password expired",
                   {Stamp("lockdate")} "Locked at",
                   CASE lockreason WHEN 0 THEN 'by an administrator' WHEN 1 THEN 'failed logins' WHEN 2 THEN 'inactive' WHEN 3 THEN 'expired' ELSE convert(varchar(11), lockreason) END "Why",
                   {Stamp("lastlogindate")} "Last login", {Stamp("pwdate")} "Password set"
            FROM master..syslogins
            WHERE status & 6 <> 0
            ORDER BY lockdate DESC
            """,
            cancellationToken);
        var unlock = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(logins))
        {
            var name = ToolReports.Cell(columns, row, "Login");
            if (name is null)
            {
                continue;
            }

            if (ToolReports.Cell(columns, row, "Why") == "failed logins")
            {
                warnings.Add($"{name} was locked after failed logins at {ToolReports.Cell(columns, row, "Locked at")}.");
            }

            if (unlock.Count < MaxScripts && ToolReports.Cell(columns, row, "Locked") == "yes")
            {
                unlock.Add(new ToolScript($"Unlock {name}", $"exec sp_locklogin '{name.Replace("'", "''", StringComparison.Ordinal)}', 'unlock'"));
            }
        }

        logins = logins with { Scripts = unlock };

        // Users in each database whose login is gone, or whose login now has another name (as after a load from another server).
        var orphans = new List<string?[]>();
        var renamed = new List<string?[]>();
        var drop = new List<ToolScript>();
        foreach (var db in await OnlineDatabasesAsync(connection, cancellationToken))
        {
            try
            {
                var users = await DbQuery.ListAsync(
                    connection,
                    $"""
                    SELECT u.name, u.suid, l.name
                    FROM {AseDba.Quote(db)}..sysusers u LEFT JOIN master..syslogins l ON l.suid = u.suid
                    WHERE u.suid > 1 AND u.uid <> u.gid AND (l.suid IS NULL OR l.name <> u.name)
                    ORDER BY u.name
                    """,
                    [],
                    r => (User: DbQuery.Text(r, 0)!, Suid: DbQuery.Int(r, 1), Login: DbQuery.Text(r, 2)),
                    cancellationToken);
                foreach (var user in users)
                {
                    if (user.Login is null)
                    {
                        orphans.Add([db, user.User, user.Suid.ToString(CultureInfo.InvariantCulture)]);
                        if (drop.Count < MaxScripts)
                        {
                            drop.Add(new ToolScript($"Drop {db}.{user.User}", $"use {AseDba.Quote(db)}\ngo\nexec sp_dropuser '{user.User.Replace("'", "''", StringComparison.Ordinal)}'"));
                        }
                    }
                    else
                    {
                        renamed.Add([db, user.User, user.Login]);
                    }
                }
            }
            catch (DbException)
            {
                // A database this login can't use: left out.
            }
        }

        if (orphans.Count > 0)
        {
            warnings.Add($"{orphans.Count} user{(orphans.Count == 1 ? " has" : "s have")} no login (e.g. {orphans[0][0]}.{orphans[0][1]}): nobody can sign in as them.");
        }

        if (renamed.Count > 0)
        {
            warnings.Add($"{renamed.Count} user{(renamed.Count == 1 ? " is" : "s are")} mapped to a login of another name (e.g. {renamed[0][0]}.{renamed[0][1]} → {renamed[0][2]}): after a load from another server, that's someone else.");
        }

        IReadOnlyList<SqlColumn> orphanColumns = [new("Database", "varchar", false), new("User", "varchar", false), new("Login id (suid)", "int", true)];
        IReadOnlyList<SqlColumn> renamedColumns = [new("Database", "varchar", false), new("User", "varchar", false), new("Signs in as login", "varchar", false)];
        var orphanSection = new ToolSection(
            "Users without a login",
            "sysusers, master..syslogins",
            [new SqlOutput("results", Columns: orphanColumns, Rows: orphans, RowCount: orphans.Count)],
            null,
            drop,
            "Their login was dropped, or the database came from another server where the ids differ. Drop them, or add a login with that id.");
        var renamedSection = new ToolSection(
            "Users whose login has another name",
            "sysusers, master..syslogins",
            [new SqlOutput("results", Columns: renamedColumns, Rows: renamed, RowCount: renamed.Count)],
            null,
            null,
            "A user maps to a login by its id (suid), not its name. Often deliberate; after loading a database from another server, check each one.");

        var idle = await SectionAsync(
            connection,
            "Logins not used for 90 days",
            "master..syslogins",
            $"""
            SELECT name "Login", {Stamp("lastlogindate")} "Last login", {Stamp("crdate")} "Created", dbname "Default database"
            FROM master..syslogins
            WHERE status & 2 = 0 AND lastlogindate < dateadd(dd, -90, getdate())
            ORDER BY lastlogindate
            """,
            cancellationToken,
            note: "Last logins are recorded while 'enable last login updates' is on.");

        return new ToolReport(warnings, [], [logins, orphanSection, renamedSection, idle], now);
    }

    // ---- Maintenance ----

    private static async Task<ToolReport> MaintenanceAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var rows = new List<string?[]>();
        var stale = new List<(string Db, string Owner, string Table, long Rows, DateTime? Updated)>();
        foreach (var db in (await OnlineDatabasesAsync(connection, cancellationToken)).Where(d => !NotDumped.Contains(d) && !d.Equals("master", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var q = AseDba.Quote(db);
                var tables = await DbQuery.ListAsync(
                    connection,
                    $"""
                    SELECT TOP 15 u.name, o.name, row_count(db_id(@db), o.id),
                           reserved_pages(db_id(@db), o.id) * (@@maxpagesize / 1048576.0),
                           (SELECT max(s.moddate) FROM {q}..sysstatistics s WHERE s.id = o.id)
                    FROM {q}..sysobjects o JOIN {q}..sysusers u ON u.uid = o.uid
                    WHERE o.type = 'U'
                    ORDER BY reserved_pages(db_id(@db), o.id) DESC
                    """,
                    [("@db", db)],
                    r => (
                        Owner: DbQuery.Text(r, 0)!,
                        Table: DbQuery.Text(r, 1)!,
                        Rows: DbQuery.Long(r, 2) ?? 0,
                        Mb: r.IsDBNull(3) ? 0 : Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture),
                        Updated: r.IsDBNull(4) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture)),
                    cancellationToken);
                foreach (var t in tables)
                {
                    var age = t.Updated is { } u ? (now.LocalDateTime - u).TotalDays : (double?)null;
                    rows.Add(
                    [
                        db,
                        $"{t.Owner}.{t.Table}",
                        t.Rows.ToString(CultureInfo.InvariantCulture),
                        AseDba.Mb(t.Mb),
                        t.Updated?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        age is { } a ? a.ToString("0", CultureInfo.InvariantCulture) : null,
                    ]);
                    if (t.Rows >= 100_000 && (age is null || age > 30))
                    {
                        stale.Add((db, t.Owner, t.Table, t.Rows, t.Updated));
                    }
                }
            }
            catch (DbException)
            {
                // A database this login can't use: left out.
            }
        }

        if (stale.Count > 0)
        {
            var oldest = stale.OrderBy(s => s.Updated ?? DateTime.MinValue).First();
            warnings.Add(
                $"{stale.Count} large table{(stale.Count == 1 ? " has" : "s have")} statistics older than 30 days; the oldest is {oldest.Db}.{oldest.Owner}.{oldest.Table} " +
                $"({(oldest.Updated is { } u ? $"updated {u:yyyy-MM-dd HH:mm}" : "never updated")}). The optimizer plans from them.");
        }

        IReadOnlyList<SqlColumn> columns =
        [
            new("Database", "varchar", false),
            new("Table", "varchar", false),
            new("Rows", "bigint", true),
            new("Reserved MB", "numeric", true),
            new("Statistics updated", "datetime", false),
            new("Days since", "int", true),
        ];
        var scripts = stale.Take(MaxScripts)
            .Select(s => new ToolScript($"Update statistics: {s.Db}.{s.Table}", $"use {AseDba.Quote(s.Db)}\ngo\nupdate index statistics {AseDba.Quote(s.Owner)}.{AseDba.Quote(s.Table)}"))
            .Append(new ToolScript("Check a database's consistency", "dbcc checkdb(dbname)"))
            .Append(new ToolScript("Rebuild a table (reclaims space, re-sorts)", "reorg rebuild owner.tablename"))
            .ToList();
        var largest = new ToolSection(
            "Largest tables, and their statistics",
            "sysobjects, row_count(), reserved_pages(), sysstatistics",
            [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
            null,
            scripts,
            "The 15 largest tables of each user database.");
        return new ToolReport(warnings, [], [largest], now);
    }

    // ---- Helpers ----

    /// <summary>A datetime as "yyyy-mm-dd hh:mm:ss" (style 23 has a T in it).</summary>
    private static string Stamp(string expression) => $"str_replace(convert(varchar(19), {expression}, 23), 'T', ' ')";

    private static async Task<ToolSection> SectionAsync(
        DbConnection connection, string title, string source, string sql, CancellationToken cancellationToken, string? fallbackSql = null, string? note = null)
    {
        var section = await AseReplication.SectionAsync(connection, title, sql, cancellationToken);
        if (section.Error is not null && fallbackSql is not null)
        {
            var fallback = await AseReplication.SectionAsync(connection, title, fallbackSql, cancellationToken);
            if (fallback.Error is null)
            {
                return fallback with { Command = source, Note = $"{note} Monitoring couldn't be read ({section.Error.Trim()}): showing what the server keeps itself.".Trim() };
            }
        }

        return section with { Command = source, Note = note };
    }

    private static async Task<IReadOnlyList<string>> OnlineDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection, "SELECT name FROM master..sysdatabases WHERE status2 & 16 = 0 AND status & 32 = 0 ORDER BY name", [], r => DbQuery.Text(r, 0)!, cancellationToken);

    private static async Task<DateTime?> ScalarDateAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            var found = await DbQuery.ListAsync(connection, sql, [], r => r.IsDBNull(0) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(0), CultureInfo.InvariantCulture), cancellationToken);
            return found.Count > 0 ? found[0] : null;
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static async Task<int?> ScalarIntAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            var found = await DbQuery.ListAsync(connection, sql, [], r => r.IsDBNull(0) ? (int?)null : DbQuery.Int(r, 0), cancellationToken);
            return found.Count > 0 ? found[0] : null;
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static string Shorten(string? text) =>
        text is null ? string.Empty : text.ReplaceLineEndings(" ").Trim() is var one && one.Length > 160 ? $"{one[..157]}…" : one;
}
