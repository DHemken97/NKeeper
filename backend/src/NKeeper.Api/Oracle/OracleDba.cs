using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Oracle.ManagedDataAccess.Client;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Oracle;

/// <summary>
/// Oracle's Management reports: Storage (tablespaces, temp, datafiles), Backups (RMAN history, datafile
/// backups, fast recovery area, configuration) and Health (instance, limits, alerts, alert log, invalid
/// objects, accounts, long operations). They read the DBA_ and V$ views, so the login needs
/// SELECT_CATALOG_ROLE (or SELECT ANY DICTIONARY); a section it can't read shows why.
/// Scripts are offered, never run: SQL opens in a query window; RMAN commands are copied, as RMAN runs on the server.
/// </summary>
internal static class OracleDba
{
    public const string Storage = "storage";
    public const string Backups = "backups";
    public const string Health = "health";

    private const int RowsPerResult = 500;
    private const int MaxScripts = 20;

    public static IReadOnlyList<SqlObjectNode> ToolNodes =>
    [
        new() { Kind = Kinds.Tool, Label = "Storage", Detail = "Tablespaces and datafiles", Name = Storage },
        new() { Kind = Kinds.Tool, Label = "Backups (RMAN)", Detail = "Backup history and recovery area", Name = Backups },
        new() { Kind = Kinds.Tool, Label = "Health", Detail = "Limits, alerts, invalid objects", Name = Health },
    ];

    public static Task<ToolReport> ReportAsync(DbConnection connection, string tool, DateTimeOffset now, CancellationToken cancellationToken) => tool switch
    {
        Storage => StorageAsync(connection, now, cancellationToken),
        Backups => BackupsAsync(connection, now, cancellationToken),
        Health => HealthAsync(connection, now, cancellationToken),
        _ => throw new SqlExplorerException($"There's no “{tool}” report."),
    };

    // ---- Storage ----

    private static async Task<ToolReport> StorageAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var tablespaces = await SectionAsync(
            connection,
            "Tablespaces",
            "dba_tablespaces, dba_data_files, dba_free_space",
            """
            SELECT d.tablespace_name "Tablespace", d.contents "Contents", d.status "Status", d.bigfile "Bigfile",
                   ROUND(f.bytes / 1048576) "Size MB",
                   ROUND((f.bytes - NVL(s.free, 0)) / 1048576) "Used MB",
                   ROUND(NVL(s.free, 0) / 1048576) "Free MB",
                   ROUND(100 * (f.bytes - NVL(s.free, 0)) / f.bytes, 1) "Used %",
                   ROUND(f.maxbytes / 1048576) "Max MB",
                   ROUND(100 * (f.bytes - NVL(s.free, 0)) / NULLIF(f.maxbytes, 0), 1) "Used % of max",
                   f.files "Files", f.autoext "Autoextend"
            FROM dba_tablespaces d
            JOIN (SELECT tablespace_name, SUM(bytes) bytes,
                         SUM(CASE WHEN autoextensible = 'YES' THEN GREATEST(bytes, maxbytes) ELSE bytes END) maxbytes,
                         COUNT(*) files, MAX(autoextensible) autoext
                  FROM dba_data_files GROUP BY tablespace_name) f ON f.tablespace_name = d.tablespace_name
            LEFT JOIN (SELECT tablespace_name, SUM(bytes) free FROM dba_free_space GROUP BY tablespace_name) s ON s.tablespace_name = d.tablespace_name
            ORDER BY 10 DESC NULLS LAST
            """,
            cancellationToken,
            note: "Undo tablespaces count unexpired undo as used; it's reused as it expires.");

        var full = new List<string>();
        foreach (var (columns, row) in ToolReports.Rows(tablespaces))
        {
            var name = ToolReports.Cell(columns, row, "Tablespace") ?? "?";
            var ofMax = ToolReports.Number(columns, row, "Used % of max") ?? 0;
            var contents = ToolReports.Cell(columns, row, "Contents");
            if (ofMax >= 85)
            {
                warnings.Add($"{name} is {ofMax:0.#}% of its maximum size ({ToolReports.Cell(columns, row, "Max MB")} MB).");
            }

            if (string.Equals(ToolReports.Cell(columns, row, "Status"), "OFFLINE", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{name} is offline.");
            }

            if (ofMax >= 70 && !string.Equals(contents, "TEMPORARY", StringComparison.OrdinalIgnoreCase))
            {
                full.Add(name);
            }
        }

        if (tablespaces.Error is null && full.Count > 0)
        {
            tablespaces = tablespaces with { Scripts = await GrowthScriptsAsync(connection, full, cancellationToken) };
        }

        var temp = await SectionAsync(
            connection,
            "Temporary tablespaces",
            "dba_temp_files, v$tempseg_usage",
            """
            SELECT t.tablespace_name "Tablespace",
                   ROUND(SUM(t.bytes) / 1048576) "Size MB",
                   ROUND(SUM(CASE WHEN t.autoextensible = 'YES' THEN GREATEST(t.bytes, t.maxbytes) ELSE t.bytes END) / 1048576) "Max MB",
                   (SELECT ROUND(NVL(SUM(u.blocks * ts.block_size), 0) / 1048576)
                    FROM v$tempseg_usage u JOIN dba_tablespaces ts ON ts.tablespace_name = u.tablespace
                    WHERE u.tablespace = t.tablespace_name) "In use MB"
            FROM dba_temp_files t
            GROUP BY t.tablespace_name
            ORDER BY 1
            """,
            cancellationToken);
        foreach (var (columns, row) in ToolReports.Rows(temp))
        {
            var max = ToolReports.Number(columns, row, "Max MB") ?? 0;
            var used = ToolReports.Number(columns, row, "In use MB") ?? 0;
            if (max > 0 && used / max >= 0.9)
            {
                warnings.Add($"Temporary tablespace {ToolReports.Cell(columns, row, "Tablespace")} is {100 * used / max:0}% in use.");
            }
        }

        var files = await SectionAsync(
            connection,
            "Datafiles",
            "dba_data_files",
            """
            SELECT f.file_id "File", f.tablespace_name "Tablespace", f.file_name "Path",
                   ROUND(f.bytes / 1048576) "Size MB", f.autoextensible "Autoextend",
                   ROUND(f.maxbytes / 1048576) "Max MB",
                   ROUND(f.increment_by * t.block_size / 1048576) "Next MB",
                   f.status "Status", f.online_status "Online"
            FROM dba_data_files f JOIN dba_tablespaces t ON t.tablespace_name = f.tablespace_name
            ORDER BY f.tablespace_name, f.file_id
            """,
            cancellationToken);

        return new ToolReport(warnings, [], [tablespaces, temp, files], now);
    }

    /// <summary>For tablespaces filling up: add a datafile (or grow a bigfile one), and let its fixed-size files autoextend.</summary>
    private static async Task<IReadOnlyList<ToolScript>> GrowthScriptsAsync(DbConnection connection, IReadOnlyList<string> tablespaces, CancellationToken cancellationToken)
    {
        var scripts = new List<ToolScript>();
        try
        {
            var omf = (await DbQuery.ListAsync(connection, "SELECT value FROM v$parameter WHERE name = 'db_create_file_dest'", [], r => DbQuery.Text(r, 0), cancellationToken))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var files = await DbQuery.ListAsync(
                connection,
                """
                SELECT f.tablespace_name, f.file_name, f.autoextensible, t.bigfile
                FROM dba_data_files f JOIN dba_tablespaces t ON t.tablespace_name = f.tablespace_name
                ORDER BY f.tablespace_name, f.file_id
                """,
                [],
                r => (Tablespace: DbQuery.Text(r, 0)!, Path: DbQuery.Text(r, 1)!, Autoextend: DbQuery.Text(r, 2) == "YES", Bigfile: DbQuery.Text(r, 3) == "YES"),
                cancellationToken);

            foreach (var name in tablespaces)
            {
                var own = files.Where(f => f.Tablespace == name).ToList();
                if (own.Count == 0)
                {
                    continue;
                }

                if (own[0].Bigfile)
                {
                    scripts.Add(new ToolScript($"{name}: let it grow", $"ALTER TABLESPACE {Identifier(name)} AUTOEXTEND ON NEXT 1G MAXSIZE UNLIMITED;"));
                }
                else
                {
                    var target = omf is not null ? string.Empty : $" {Literal(NewFilePath(own[^1].Path, name, own.Count + 1))}";
                    scripts.Add(new ToolScript(
                        $"{name}: add a datafile",
                        $"ALTER TABLESPACE {Identifier(name)} ADD DATAFILE{target} SIZE 1G AUTOEXTEND ON NEXT 256M MAXSIZE UNLIMITED;"));
                }

                foreach (var file in own.Where(f => !f.Autoextend))
                {
                    scripts.Add(new ToolScript($"{name}: autoextend {Path.GetFileName(file.Path)}", $"ALTER DATABASE DATAFILE {Literal(file.Path)} AUTOEXTEND ON NEXT 256M MAXSIZE UNLIMITED;"));
                }

                if (scripts.Count >= MaxScripts)
                {
                    break;
                }
            }
        }
        catch (DbException)
        {
            // Without v$parameter, offer no scripts: the report still shows.
        }

        return scripts;
    }

    /// <summary>A name for the next datafile beside the last one (in its ASM disk group, if it's in one).</summary>
    internal static string NewFilePath(string lastFile, string tablespace, int number)
    {
        if (lastFile.StartsWith('+'))
        {
            var slash = lastFile.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? lastFile : lastFile[..slash];
        }

        var cut = Math.Max(lastFile.LastIndexOf('/'), lastFile.LastIndexOf('\\'));
        var directory = cut < 0 ? string.Empty : lastFile[..(cut + 1)];
        return $"{directory}{tablespace.ToLowerInvariant()}{number.ToString("00", CultureInfo.InvariantCulture)}.dbf";
    }

    // ---- Backups ----

    private static readonly string[] DatabaseBackupTypes = ["DB FULL", "DB INCR", "DATAFILE FULL", "DATAFILE INCR"];

    private static async Task<ToolReport> BackupsAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var database = await SectionAsync(
            connection,
            "Database",
            "v$database",
            """
            SELECT name "Database", db_unique_name "Unique name", log_mode "Log mode", database_role "Role",
                   open_mode "Open mode", flashback_on "Flashback", force_logging "Force logging"
            FROM v$database
            """,
            cancellationToken);
        var archiveLog = true;
        foreach (var (columns, row) in ToolReports.Rows(database))
        {
            if (string.Equals(ToolReports.Cell(columns, row, "Log mode"), "NOARCHIVELOG", StringComparison.OrdinalIgnoreCase))
            {
                archiveLog = false;
                warnings.Add("The database is in NOARCHIVELOG mode: it can only be backed up while closed (mounted), and only restored to the time of a backup.");
            }
        }

        var jobs = await SectionAsync(
            connection,
            "RMAN jobs (14 days)",
            "v$rman_backup_job_details",
            """
            SELECT TO_CHAR(start_time, 'YYYY-MM-DD HH24:MI') "Started", ROUND((SYSDATE - end_time) * 24, 1) "Hours ago",
                   input_type "Type", status "Status", time_taken_display "Took",
                   input_bytes_display "Read", output_bytes_display "Written", output_device_type "Device", session_recid "Session"
            FROM v$rman_backup_job_details
            WHERE start_time > SYSDATE - 14
            ORDER BY start_time DESC
            """,
            cancellationToken);
        if (jobs.Error is null)
        {
            double? lastDatabase = null;
            var failed = 0;
            foreach (var (columns, row) in ToolReports.Rows(jobs))
            {
                var status = ToolReports.Cell(columns, row, "Status") ?? string.Empty;
                var hours = ToolReports.Number(columns, row, "Hours ago");
                var type = ToolReports.Cell(columns, row, "Type");
                if (status.StartsWith("COMPLETED", StringComparison.OrdinalIgnoreCase) && !status.Contains("ERRORS", StringComparison.OrdinalIgnoreCase)
                    && DatabaseBackupTypes.Contains(type, StringComparer.OrdinalIgnoreCase) && hours is { } h)
                {
                    lastDatabase = lastDatabase is null ? h : Math.Min(lastDatabase.Value, h);
                }

                if ((status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || status.Contains("ERRORS", StringComparison.OrdinalIgnoreCase)) && (hours ?? 0) <= 24 * 7)
                {
                    failed++;
                }
            }

            if (lastDatabase is null)
            {
                warnings.Insert(0, "No database backup has completed in the last 14 days (by the control file's RMAN history).");
            }
            else if (lastDatabase > 26)
            {
                warnings.Insert(0, $"The last database backup finished {FormatHours(lastDatabase.Value)} ago.");
            }

            if (failed > 0)
            {
                warnings.Add($"{failed} RMAN job{(failed == 1 ? "" : "s")} failed or finished with errors in the last 7 days.");
            }
        }

        var running = await SectionAsync(
            connection,
            "RMAN in progress",
            "v$session_longops",
            """
            SELECT sid "SID", opname "Operation", ROUND(100 * sofar / NULLIF(totalwork, 0), 1) "Done %",
                   elapsed_seconds "Elapsed s", time_remaining "Left s"
            FROM v$session_longops
            WHERE opname LIKE 'RMAN%' AND sofar < totalwork
            ORDER BY start_time
            """,
            cancellationToken);

        var datafiles = await SectionAsync(
            connection,
            "Last backup of each datafile",
            "v$datafile, v$backup_datafile",
            """
            SELECT f.file# "File", t.name "Tablespace", f.name "Path",
                   TO_CHAR(MAX(b.completion_time), 'YYYY-MM-DD HH24:MI') "Last backup",
                   ROUND((SYSDATE - MAX(b.completion_time)) * 24, 1) "Hours ago"
            FROM v$datafile f
            JOIN v$tablespace t ON t.ts# = f.ts# AND t.con_id = f.con_id
            LEFT JOIN v$backup_datafile b ON b.file# = f.file#
            GROUP BY f.file#, t.name, f.name
            ORDER BY MAX(b.completion_time) NULLS FIRST, f.file#
            """,
            cancellationToken,
            fallbackSql: """
            SELECT f.file# "File", t.name "Tablespace", f.name "Path",
                   TO_CHAR(MAX(b.completion_time), 'YYYY-MM-DD HH24:MI') "Last backup",
                   ROUND((SYSDATE - MAX(b.completion_time)) * 24, 1) "Hours ago"
            FROM v$datafile f
            JOIN v$tablespace t ON t.ts# = f.ts#
            LEFT JOIN v$backup_datafile b ON b.file# = f.file#
            GROUP BY f.file#, t.name, f.name
            ORDER BY MAX(b.completion_time) NULLS FIRST, f.file#
            """,
            note: "From the control file: backups taken with a recovery catalog only, or aged out of the control file, aren't listed.");
        var never = ToolReports.Rows(datafiles).Count(x => string.IsNullOrEmpty(ToolReports.Cell(x.Columns, x.Row, "Last backup")));
        if (never > 0)
        {
            warnings.Add($"{never} datafile{(never == 1 ? " has" : "s have")} no backup on record.");
        }

        var recoveryArea = await SectionAsync(
            connection,
            "Fast recovery area",
            "v$recovery_file_dest",
            """
            SELECT name "Location", ROUND(space_limit / 1048576) "Limit MB", ROUND(space_used / 1048576) "Used MB",
                   ROUND(space_reclaimable / 1048576) "Reclaimable MB",
                   ROUND(100 * (space_used - space_reclaimable) / NULLIF(space_limit, 0), 1) "Used % (not reclaimable)",
                   number_of_files "Files"
            FROM v$recovery_file_dest
            WHERE space_limit > 0
            """,
            cancellationToken,
            note: "When it fills, archiving stops and the database waits until space is freed (DELETE OBSOLETE, or back up and delete archive logs).");
        foreach (var (columns, row) in ToolReports.Rows(recoveryArea))
        {
            if (ToolReports.Number(columns, row, "Used % (not reclaimable)") is >= 85 and var used)
            {
                warnings.Insert(0, $"The fast recovery area is {used:0.#}% full (not counting reclaimable files).");
            }
        }

        var areaUsage = await SectionAsync(
            connection,
            "Recovery area by file type",
            "v$recovery_area_usage",
            """
            SELECT file_type "Type", percent_space_used "Used %", percent_space_reclaimable "Reclaimable %", number_of_files "Files"
            FROM v$recovery_area_usage
            WHERE number_of_files > 0
            ORDER BY percent_space_used DESC
            """,
            cancellationToken);

        var configuration = await SectionAsync(
            connection,
            "RMAN configuration",
            "v$rman_configuration",
            "SELECT name \"Setting\", value \"Value\" FROM v$rman_configuration ORDER BY conf#",
            cancellationToken,
            note: "Only settings changed from RMAN's defaults (SHOW ALL lists every one).");

        var output = await SectionAsync(
            connection,
            "Output of the last RMAN job",
            "v$rman_output",
            """
            SELECT output "Output" FROM v$rman_output
            WHERE session_recid = (SELECT MAX(session_recid) FROM v$rman_backup_job_details)
            ORDER BY recid
            """,
            cancellationToken,
            note: "Kept in memory since the instance started.");

        database = database with { Scripts = RmanScripts(archiveLog) };
        return new ToolReport(warnings, [], [database, jobs, running, datafiles, recoveryArea, areaUsage, configuration, output], now);
    }

    private static IReadOnlyList<ToolScript> RmanScripts(bool archiveLog) =>
    [
        .. archiveLog
            ? new[]
            {
                new ToolScript("Full backup with archive logs", "BACKUP DATABASE PLUS ARCHIVELOG;", "copy"),
                new ToolScript("Level 0 incremental", "BACKUP INCREMENTAL LEVEL 0 DATABASE PLUS ARCHIVELOG;", "copy"),
                new ToolScript("Level 1 incremental", "BACKUP INCREMENTAL LEVEL 1 DATABASE PLUS ARCHIVELOG;", "copy"),
                new ToolScript("Archive logs not yet backed up", "BACKUP ARCHIVELOG ALL NOT BACKED UP 1 TIMES;", "copy"),
            }
            : [new ToolScript("Offline (consistent) backup", "SHUTDOWN IMMEDIATE;\nSTARTUP MOUNT;\nBACKUP DATABASE;\nALTER DATABASE OPEN;", "copy")],
        new ToolScript("Crosscheck and remove expired", "CROSSCHECK BACKUP;\nCROSSCHECK ARCHIVELOG ALL;\nDELETE NOPROMPT EXPIRED BACKUP;\nDELETE NOPROMPT EXPIRED ARCHIVELOG ALL;", "copy"),
        new ToolScript("Delete obsolete backups", "REPORT OBSOLETE;\nDELETE NOPROMPT OBSOLETE;", "copy"),
        new ToolScript("What needs a backup", "REPORT NEED BACKUP;\nREPORT UNRECOVERABLE;", "copy"),
        new ToolScript("Check the backups restore", "RESTORE DATABASE VALIDATE;\nRESTORE ARCHIVELOG ALL VALIDATE;", "copy"),
        new ToolScript("Check for corrupt blocks", "BACKUP VALIDATE CHECK LOGICAL DATABASE;", "copy"),
        new ToolScript("Show all settings", "SHOW ALL;", "copy"),
        new ToolScript("Corrupt blocks found", "SELECT * FROM v$database_block_corruption;"),
    ];

    // ---- Health ----

    private static async Task<ToolReport> HealthAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var instance = await SectionAsync(
            connection,
            "Instance",
            "v$instance",
            """
            SELECT instance_name "Instance", host_name "Host", version "Version", status "Status",
                   TO_CHAR(startup_time, 'YYYY-MM-DD HH24:MI') "Started", ROUND(SYSDATE - startup_time, 1) "Up (days)",
                   database_status "Database status"
            FROM v$instance
            """,
            cancellationToken);

        var size = await SectionAsync(
            connection,
            "Size",
            "dba_data_files, dba_temp_files, v$log, dba_segments",
            """
            SELECT ROUND((SELECT SUM(bytes) FROM dba_data_files) / 1073741824, 1) "Datafiles GB",
                   ROUND((SELECT NVL(SUM(bytes), 0) FROM dba_temp_files) / 1073741824, 1) "Temp GB",
                   ROUND((SELECT SUM(bytes * members) FROM v$log) / 1073741824, 1) "Redo GB",
                   ROUND((SELECT SUM(bytes) FROM dba_segments) / 1073741824, 1) "Used by segments GB"
            FROM dual
            """,
            cancellationToken);

        var limits = await SectionAsync(
            connection,
            "Sessions and processes",
            "v$resource_limit",
            """
            SELECT resource_name "Resource", current_utilization "Now", max_utilization "Highest", TRIM(limit_value) "Limit",
                   CASE WHEN TRIM(limit_value) NOT IN ('UNLIMITED', '0') THEN ROUND(100 * current_utilization / TO_NUMBER(TRIM(limit_value)), 1) END "Used %"
            FROM v$resource_limit
            WHERE resource_name IN ('sessions', 'processes', 'transactions', 'max_shared_servers', 'parallel_max_servers')
            ORDER BY resource_name
            """,
            cancellationToken);
        foreach (var (columns, row) in ToolReports.Rows(limits))
        {
            if (ToolReports.Number(columns, row, "Used %") is >= 85 and var used)
            {
                warnings.Add($"{ToolReports.Cell(columns, row, "Resource")} is at {used:0}% of its limit ({ToolReports.Cell(columns, row, "Limit")}): new connections will be refused when it's reached.");
            }
        }

        var alerts = await SectionAsync(
            connection,
            "Outstanding alerts",
            "dba_outstanding_alerts",
            """
            SELECT TO_CHAR(creation_time, 'YYYY-MM-DD HH24:MI') "Raised", object_type "Type", object_name "Object",
                   reason "Reason", suggested_action "Suggested action"
            FROM dba_outstanding_alerts
            ORDER BY creation_time DESC
            """,
            cancellationToken);
        var alertCount = ToolReports.Rows(alerts).Count();
        if (alertCount > 0)
        {
            warnings.Add($"{alertCount} outstanding server alert{(alertCount == 1 ? "" : "s")}.");
        }

        var alertLog = await SectionAsync(
            connection,
            "Alert log errors (24 hours)",
            "v$diag_alert_ext",
            """
            SELECT * FROM (
                SELECT TO_CHAR(originating_timestamp, 'YYYY-MM-DD HH24:MI:SS') "Time", SUBSTR(message_text, 1, 400) "Message"
                FROM v$diag_alert_ext
                WHERE originating_timestamp > SYSTIMESTAMP - INTERVAL '1' DAY
                  AND (message_text LIKE '%ORA-%' OR message_text LIKE '%TNS-%' OR message_text LIKE '%Checkpoint not complete%' OR message_text LIKE '%orrupt%')
                ORDER BY originating_timestamp DESC)
            WHERE ROWNUM <= 200
            """,
            cancellationToken,
            note: "Oracle 12c and later; on 11g, read the alert log on the server (adrci).");
        var errors = ToolReports.Rows(alertLog).Count();
        if (errors > 0)
        {
            warnings.Add($"{errors} error{(errors == 1 ? "" : "s")} in the alert log in the last 24 hours.");
        }

        var switches = await SectionAsync(
            connection,
            "Redo log switches per hour (24 hours)",
            "v$log_history",
            """
            SELECT TO_CHAR(first_time, 'YYYY-MM-DD HH24') || ':00' "Hour", COUNT(*) "Switches"
            FROM v$log_history
            WHERE first_time > SYSDATE - 1
            GROUP BY TO_CHAR(first_time, 'YYYY-MM-DD HH24')
            ORDER BY 1 DESC
            """,
            cancellationToken,
            note: "More than a few switches an hour usually means the redo logs are too small.");
        var busiest = ToolReports.Rows(switches).Select(x => ToolReports.Number(x.Columns, x.Row, "Switches") ?? 0).DefaultIfEmpty(0).Max();
        if (busiest >= 20)
        {
            warnings.Add($"Up to {busiest:0} redo log switches in an hour: consider larger redo logs.");
        }

        var invalid = await SectionAsync(
            connection,
            "Invalid objects",
            "dba_objects",
            """
            SELECT o.owner "Owner", o.object_type "Type", COUNT(*) "Invalid"
            FROM dba_objects o JOIN dba_users u ON u.username = o.owner
            WHERE o.status = 'INVALID' AND u.oracle_maintained = 'N'
            GROUP BY o.owner, o.object_type
            ORDER BY 3 DESC
            """,
            cancellationToken,
            fallbackSql: """
            SELECT owner "Owner", object_type "Type", COUNT(*) "Invalid"
            FROM dba_objects
            WHERE status = 'INVALID'
            GROUP BY owner, object_type
            ORDER BY 3 DESC
            """);
        var owners = ToolReports.Rows(invalid).Select(x => ToolReports.Cell(x.Columns, x.Row, "Owner")).OfType<string>().Distinct().Take(MaxScripts).ToList();
        if (owners.Count > 0)
        {
            invalid = invalid with
            {
                Scripts =
                [
                    .. owners.Select(owner => new ToolScript($"Recompile {owner}", $"BEGIN\n  DBMS_UTILITY.COMPILE_SCHEMA(schema => {Literal(owner)}, compile_all => FALSE);\nEND;\n/")),
                    new ToolScript("Recompile everything (SQL*Plus as SYSDBA)", "@?/rdbms/admin/utlrp.sql", "copy"),
                    new ToolScript("Why they're invalid", "SELECT owner, name, type, line, position, text FROM dba_errors ORDER BY owner, name, sequence;"),
                ],
            };
        }

        var accounts = await SectionAsync(
            connection,
            "Accounts locked, expired or expiring (14 days)",
            "dba_users",
            """
            SELECT username "User", account_status "Status", TO_CHAR(lock_date, 'YYYY-MM-DD HH24:MI') "Locked",
                   TO_CHAR(expiry_date, 'YYYY-MM-DD') "Expires", profile "Profile"
            FROM dba_users
            WHERE oracle_maintained = 'N' AND (account_status <> 'OPEN' OR expiry_date < SYSDATE + 14)
            ORDER BY lock_date DESC NULLS LAST, username
            """,
            cancellationToken,
            fallbackSql: """
            SELECT username "User", account_status "Status", TO_CHAR(lock_date, 'YYYY-MM-DD HH24:MI') "Locked",
                   TO_CHAR(expiry_date, 'YYYY-MM-DD') "Expires", profile "Profile"
            FROM dba_users
            WHERE account_status <> 'EXPIRED & LOCKED' AND (account_status <> 'OPEN' OR expiry_date < SYSDATE + 14)
            ORDER BY lock_date DESC NULLS LAST, username
            """);
        var unlock = new List<ToolScript>();
        foreach (var (columns, row) in ToolReports.Rows(accounts))
        {
            var user = ToolReports.Cell(columns, row, "User");
            var status = ToolReports.Cell(columns, row, "Status") ?? string.Empty;
            if (user is null)
            {
                continue;
            }

            if (status.Contains("TIMED", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{user} is locked after failed sign-ins.");
            }

            if (unlock.Count < MaxScripts && status.Contains("LOCKED", StringComparison.OrdinalIgnoreCase))
            {
                unlock.Add(new ToolScript($"Unlock {user}", $"ALTER USER {Identifier(user)} ACCOUNT UNLOCK;"));
            }
            else if (unlock.Count < MaxScripts && status.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                unlock.Add(new ToolScript($"New password for {user}", $"ALTER USER {Identifier(user)} IDENTIFIED BY \"new password here\";"));
            }
        }

        if (unlock.Count > 0)
        {
            accounts = accounts with { Scripts = unlock };
        }

        var longOps = await SectionAsync(
            connection,
            "Long-running operations",
            "v$session_longops",
            """
            SELECT sid "SID", username "User", opname "Operation", target "Target",
                   ROUND(100 * sofar / NULLIF(totalwork, 0), 1) "Done %", elapsed_seconds "Elapsed s", time_remaining "Left s"
            FROM v$session_longops
            WHERE sofar < totalwork
            ORDER BY start_time
            """,
            cancellationToken);

        return new ToolReport(warnings, [], [instance, size, limits, alerts, alertLog, switches, invalid, accounts, longOps], now);
    }

    // ---- Helpers ----

    /// <summary>
    /// Runs a query and keeps its result; a failure is the section's error, not the report's. When the query fails
    /// (e.g. a column Oracle 11g lacks) and there's a <paramref name="fallbackSql"/>, that runs instead.
    /// </summary>
    private static async Task<ToolSection> SectionAsync(
        DbConnection connection, string title, string source, string sql, CancellationToken cancellationToken, string? fallbackSql = null, string? note = null)
    {
        try
        {
            return new ToolSection(title, source, [await QueryAsync(connection, sql, cancellationToken)], null, null, note);
        }
        catch (DbException) when (fallbackSql is not null)
        {
            return await SectionAsync(connection, title, source, fallbackSql, cancellationToken, null, note);
        }
        catch (DbException ex)
        {
            return new ToolSection(title, source, [], ex.Message, null, note);
        }
    }

    private static async Task<SqlOutput> QueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = (OracleCommand)connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken);
        return await OracleDialect.ReadResultAsync(reader, RowsPerResult, cancellationToken);
    }

    private static string FormatHours(double hours) => hours >= 48 ? $"{hours / 24:0.#} days" : $"{hours:0} hours";

    internal static string Identifier(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string Literal(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
}
