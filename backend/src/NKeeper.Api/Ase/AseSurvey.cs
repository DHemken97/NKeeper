using System.Data.Common;
using NKeeper.Api.Databases;

namespace NKeeper.Api.Ase;

/// <summary>
/// SAP ASE's answers for the insurance questionnaire: the server and its databases (with the devices they're
/// on), who can change data (server roles, database owners, aliases and grants), auditing, the Job Scheduler,
/// and dumps (the last ones, dump history, dump devices and the Backup Server). Only reads.
/// </summary>
internal static class AseSurvey
{
    private const int MaxDatabases = 60;

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "tempdb", "sybsystemprocs", "sybsystemdb", "sybpcidb", "dbccdb", "dbccalt", "sybmgmtdb", "sybsecurity", "sybsyntax", "pubs2", "pubs3",
    };

    public static async Task<DatabaseSurvey> RunAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var survey = new SurveyBuilder(AseDialect.Instance, connection, cancellationToken);
        const string Product = "SAP Adaptive Server Enterprise (ASE)";

        // ---- The server ----
        var server = await survey.SectionAsync(
            "Server",
            "@@servername, @@version",
            $"""
            SELECT @@servername "Server", @@version "Version", @@maxpagesize "Page size", {Stamp("@@boottime")} "Started"
            """);
        string? version = null;
        foreach (var (columns, row) in SurveyBuilder.Rows(server))
        {
            var full = SurveyBuilder.Cell(columns, row, "Version") ?? "";
            var parts = full.Split('/');
            version = parts.Length > 1 ? parts[1].Trim() : full;
            survey.Fact(SurveyTopics.Software, $"{Product} {version}{(parts.Length > 2 ? $" ({parts[2].Trim()})" : "")}. Commercial software licensed from SAP (formerly Sybase) (the database server software; who developed the applications and their databases isn't something the server can tell).");
            survey.Fact(SurveyTopics.Software, $"Server {SurveyBuilder.Cell(columns, row, "Server")}, {SurveyBuilder.Cell(columns, row, "Page size")}-byte pages, running since {SurveyBuilder.Cell(columns, row, "Started")}{(parts.Length > 5 ? $", on {parts[5].Trim()}" : "")}.");
        }

        // ---- Databases ----
        var databaseSection = await survey.SectionAsync(
            "Databases",
            "master..sysdatabases, master..sysusages",
            $"""
            SELECT d.name "Database", {Stamp("d.crdate")} "Created", suser_name(d.suid) "Owner",
                   convert(numeric(18, 1), sum(u.size) * (@@maxpagesize / 1024.0) / 1024.0) "Size MB",
                   convert(numeric(18, 1), sum(CASE WHEN u.segmap = 4 THEN u.size ELSE 0 END) * (@@maxpagesize / 1024.0) / 1024.0) "Log MB",
                   CASE WHEN d.status2 & 16 = 16 THEN 'offline' WHEN d.status & 32 = 32 THEN 'loading' ELSE 'online' END "State",
                   CASE WHEN d.status & 8 = 8 THEN 'yes' ELSE 'no' END "Trunc log on chkpt"
            FROM master..sysdatabases d JOIN master..sysusages u ON u.dbid = d.dbid
            GROUP BY d.name, d.crdate, d.suid, d.status, d.status2
            ORDER BY d.name
            """);
        var devices = await survey.SectionAsync(
            "Database devices",
            "master..sysusages, master..sysdevices",
            """
            SELECT DISTINCT db_name(u.dbid) "Database", v.name "Device", v.phyname "Path"
            FROM master..sysusages u JOIN master..sysdevices v ON v.vdevno = u.vdevno
            ORDER BY 1, 2
            """);
        var paths = SurveyBuilder.Rows(devices)
            .GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Database") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Path")).OfType<string>().ToList(), StringComparer.OrdinalIgnoreCase);

        var databases = new List<SurveyDatabase>();
        foreach (var (columns, row) in SurveyBuilder.Rows(databaseSection))
        {
            var name = SurveyBuilder.Cell(columns, row, "Database") ?? "?";
            var system = SystemDatabases.Contains(name) || name.StartsWith("tempdb", StringComparison.OrdinalIgnoreCase);
            var files = paths.GetValueOrDefault(name) ?? [];
            var folder = files.Count > 0 ? SurveyBuilder.List(files.Select(SurveyBuilder.Folder), 4) : null;
            var size = SurveyBuilder.Number(columns, row, "Size MB");
            databases.Add(new SurveyDatabase(name, size, SurveyBuilder.Cell(columns, row, "Created"), SurveyBuilder.Cell(columns, row, "Owner"), SurveyBuilder.Cell(columns, row, "State"), folder, system));
            if (system)
            {
                continue;
            }

            survey.Fact(SurveyTopics.Contents, $"{name}: {size ?? 0:N0} MB, created {SurveyBuilder.Cell(columns, row, "Created")}, owner {SurveyBuilder.Cell(columns, row, "Owner")}, {SurveyBuilder.Cell(columns, row, "State")}.", name);
            if (files.Count > 0)
            {
                survey.Fact(SurveyTopics.Location, $"On device files {SurveyBuilder.List(files, 6)}.", name);
            }

            if (SurveyBuilder.Cell(columns, row, "Trunc log on chkpt") == "yes")
            {
                survey.Fact(SurveyTopics.ChangeLog, "'trunc log on chkpt' is on: the transaction log is emptied at each checkpoint, so changes can't be replayed from log dumps.", name);
            }
        }

        var userDatabases = databases.Where(d => !d.System && d.State == "online").Select(d => d.Name).Take(MaxDatabases).ToList();

        // ---- Who can change data ----
        var roles = await survey.SectionAsync(
            "Logins with server-wide roles",
            "master..sysloginroles, master..syssrvroles, master..syslogins",
            """
            SELECT r.name "Role", l.name "Login", CASE WHEN l.status & 2 = 2 THEN 'yes' ELSE 'no' END "Locked"
            FROM master..sysloginroles lr
            JOIN master..syssrvroles r ON r.srid = lr.srid
            JOIN master..syslogins l ON l.suid = lr.suid
            WHERE r.name IN ('sa_role', 'sso_role', 'oper_role', 'replication_role', 'dtm_tm_role')
            ORDER BY 1, 2
            """,
            note: "sa_role can read and change every database; sso_role manages logins and passwords; oper_role dumps and loads databases.");
        var sa = SurveyBuilder.Rows(roles)
            .Where(r => SurveyBuilder.Cell(r.Columns, r.Row, "Role") == "sa_role" && SurveyBuilder.Cell(r.Columns, r.Row, "Locked") != "yes")
            .Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Login"))
            .OfType<string>()
            .ToList();
        if (sa.Count > 0)
        {
            survey.Fact(SurveyTopics.Writers, $"System administrators (sa_role), who can add, change and delete data in every database: {SurveyBuilder.List(sa, 20)}.");
        }

        var writers = await survey.MergedSectionAsync(
            "Grants and aliases that can change data",
            "sysprotects, sysusers, sysalternates (each database)",
            userDatabases.Select(db => (db, WritersSql(db), (string?)null)),
            note: "Grants are counted per kind; 'public' means every user of the database. Logins aliased to another user act as that user (an alias to dbo can do everything).");
        foreach (var group in SurveyBuilder.Rows(writers).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Database") ?? "?", StringComparer.OrdinalIgnoreCase))
        {
            var parts = group
                .GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Grantee") ?? "?")
                .Select(g => $"{g.Key} ({SurveyBuilder.Cell(g.First().Columns, g.First().Row, "Kind")}: {SurveyBuilder.List(g.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Right") ?? "?"), 5)})");
            survey.Fact(SurveyTopics.Writers, $"{SurveyBuilder.List(parts, 15)}.", group.Key);
        }

        foreach (var db in databases.Where(d => userDatabases.Contains(d.Name)))
        {
            survey.Fact(SurveyTopics.Writers, $"The database owner ({db.Owner ?? "dbo"}) and system administrators can change everything.", db.Name);
        }

        // ---- Auditing ----
        var config = await survey.SectionAsync(
            "Audit and dump history settings",
            "master..sysconfigures, master..syscurconfigs",
            """
            SELECT c.name "Setting", isnull(cc.value2, convert(varchar(20), cc.value)) "Value"
            FROM master..sysconfigures c JOIN master..syscurconfigs cc ON cc.config = c.config
            WHERE c.name IN ('auditing', 'suspend audit when device full', 'enable dump history', 'dump history filename', 'enable monitoring', 'enable backupserver HA')
            ORDER BY 1
            """,
            """
            SELECT c.name "Setting", convert(varchar(20), cc.value) "Value"
            FROM master..sysconfigures c JOIN master..syscurconfigs cc ON cc.config = c.config
            WHERE c.name IN ('auditing', 'suspend audit when device full', 'enable dump history', 'enable monitoring')
            ORDER BY 1
            """);
        var settings = SurveyBuilder.Rows(config).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Setting") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SurveyBuilder.Cell(g.First().Columns, g.First().Row, "Value"), StringComparer.OrdinalIgnoreCase);
        if (settings.TryGetValue("auditing", out var auditing))
        {
            survey.Fact(SurveyTopics.ChangeLog, auditing == "1"
                ? "Auditing is on: the events chosen with sp_audit (logins, commands, access to chosen tables) are recorded in the sybsecurity database."
                : "Auditing is off: the server keeps no record of who changed what (beyond the transaction log, which is truncated after dumps).");
        }

        var audits = await survey.SectionAsync(
            "Audit options",
            "sybsecurity..sysauditoptions",
            "SELECT * FROM sybsecurity..sysauditoptions",
            optional: true);
        if (audits.Error is null && SurveyBuilder.Rows(audits).Any())
        {
            survey.Fact(SurveyTopics.ChangeLog, $"{SurveyBuilder.Plural(SurveyBuilder.Rows(audits).Count(), "audit option")} set (see the appendix).");
        }

        // ---- Jobs ----
        var jobs = await survey.SectionAsync(
            "Job Scheduler jobs",
            "sybmgmtdb..js_jobs",
            "SELECT * FROM sybmgmtdb..js_jobs",
            optional: true);
        if (jobs.Error is null)
        {
            survey.Fact(SurveyTopics.Deletion, $"ASE Job Scheduler is installed, with {SurveyBuilder.Plural(SurveyBuilder.Rows(jobs).Count(), "job")} (see the appendix for what they run).");
            survey.Fact(SurveyTopics.BackupSchedule, "ASE Job Scheduler is installed: dump jobs may be scheduled there (see the appendix).");
        }
        else
        {
            survey.Fact(SurveyTopics.Deletion, "ASE's Job Scheduler isn't installed, so the server deletes nothing on a schedule itself; scheduled deletions would be run by cron or the applications.");
            survey.Fact(SurveyTopics.BackupSchedule, "ASE has no scheduler of its own here: dumps are started by cron or scripts on the server, or by backup software.");
        }

        // ---- Dumps ----
        await DumpsAsync(survey, settings, userDatabases);
        return new DatabaseSurvey(Product, version, "SAP (licensed)", databases, survey.Facts, survey.Sections, now);
    }

    private static async Task DumpsAsync(SurveyBuilder survey, Dictionary<string, string?> settings, IReadOnlyList<string> userDatabases)
    {
        var dumps = await survey.SectionAsync(
            "Last dumps",
            "master..sysdatabases, master..monOpenDatabases",
            $"""
            SELECT d.name "Database", {Stamp("o.BackupStartTime")} "Last full dump started", {Stamp("isnull(o.LastTranLogDumpTime, d.dumptrdate)")} "Last log dump",
                   CASE WHEN o.LastBackupFailed > 0 THEN 'yes' ELSE 'no' END "Last dump failed"
            FROM master..sysdatabases d LEFT JOIN master..monOpenDatabases o ON o.DBID = d.dbid
            ORDER BY d.name
            """,
            $"""
            SELECT d.name "Database", {Stamp("d.dumptrdate")} "Last log dump"
            FROM master..sysdatabases d
            ORDER BY d.name
            """,
            note: "Full dump times come from monitoring (known since the server started); log dump times are kept by the server.");
        var full = new List<string>();
        var log = new List<string>();
        foreach (var (columns, row) in SurveyBuilder.Rows(dumps))
        {
            var db = SurveyBuilder.Cell(columns, row, "Database") ?? "?";
            if (!userDatabases.Contains(db, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastFull = SurveyBuilder.Cell(columns, row, "Last full dump started");
            var lastLog = SurveyBuilder.Cell(columns, row, "Last log dump");
            if (lastFull is not null && !lastFull.StartsWith("1900", StringComparison.Ordinal))
            {
                full.Add(db);
            }

            if (lastLog is not null && !lastLog.StartsWith("1900", StringComparison.Ordinal))
            {
                log.Add(db);
            }

            survey.Fact(SurveyTopics.BackupTypes, $"Last full dump {lastFull ?? "not known (none since the server started, or monitoring is off)"}; last transaction log dump {(lastLog is null || lastLog.StartsWith("1900", StringComparison.Ordinal) ? "never" : lastLog)}.", db);
        }

        if (full.Count > 0)
        {
            survey.Fact(SurveyTopics.BackupTypes, $"Full database dumps (dump database) of {SurveyBuilder.List(full)}.");
        }

        if (log.Count > 0)
        {
            survey.Fact(SurveyTopics.BackupTypes, $"Transaction log dumps (dump transaction: the changes since the last dump) of {SurveyBuilder.List(log)}.");
        }

        var historyOn = settings.GetValueOrDefault("enable dump history") == "1";
        var history = await survey.SectionAsync(
            "Dump history",
            "sp_dump_history",
            "exec sp_dump_history",
            optional: true,
            note: "The dump history file lists every dump and the files it was written to.");
        if (historyOn && history.Error is null)
        {
            survey.Fact(SurveyTopics.BackupIndex, $"Dump history is on: the server records every dump (database, type, time and files) in its dump history file{(settings.GetValueOrDefault("dump history filename") is { Length: > 0 } historyFile ? $" ({historyFile})" : "")}; {SurveyBuilder.Plural(SurveyBuilder.Rows(history).Count(), "entry", "entries")} now.");
            foreach (var row in SurveyBuilder.Rows(history))
            {
                foreach (var name in new[] { "Dump_File", "dump_file", "File", "Stripe" })
                {
                    if (SurveyBuilder.Cell(row.Columns, row.Row, name) is { } file)
                    {
                        survey.Fact(SurveyTopics.BackupLocation, $"Dumps written to {SurveyBuilder.Folder(file)}.");
                        if (SurveyBuilder.Remote(file) is { } remote)
                        {
                            survey.Fact(SurveyTopics.BackupRemote, $"Dumps are written to {remote}.");
                        }

                        break;
                    }
                }
            }
        }
        else
        {
            survey.Fact(SurveyTopics.BackupIndex, "Dump history is off ('enable dump history'), so the server keeps no list of past dumps; each dump file's header records its database and time, and Backup Server logs each dump to its error log.");
        }

        var dumpDevices = await survey.SectionAsync(
            "Dump devices",
            "master..sysdevices",
            "SELECT name \"Device\", phyname \"Path\", CASE WHEN cntrltype = 2 THEN 'disk' ELSE 'tape' END \"Kind\" FROM master..sysdevices WHERE status & 16 = 16 ORDER BY 1");
        foreach (var (columns, row) in SurveyBuilder.Rows(dumpDevices))
        {
            survey.Fact(SurveyTopics.BackupMedia, $"Dump device {SurveyBuilder.Cell(columns, row, "Device")}: {SurveyBuilder.Cell(columns, row, "Kind")}, {SurveyBuilder.Cell(columns, row, "Path")}.");
            survey.Fact(SurveyTopics.BackupLabels, $"Dump device {SurveyBuilder.Cell(columns, row, "Device")} ({SurveyBuilder.Cell(columns, row, "Kind")}) is named in the server.");
        }

        var backupServer = await survey.SectionAsync(
            "Backup Server",
            "master..sysservers",
            "SELECT srvname \"Name\", srvnetname \"Network name\" FROM master..sysservers WHERE srvname = 'SYB_BACKUP' OR srvclass = 7 ORDER BY 1",
            "SELECT srvname \"Name\", srvnetname \"Network name\" FROM master..sysservers WHERE srvname = 'SYB_BACKUP'");
        var backup = SurveyBuilder.Rows(backupServer).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Network name")).OfType<string>().FirstOrDefault();
        survey.Fact(SurveyTopics.BackupMedia, $"Dumps are written by SAP ASE Backup Server{(backup is null ? "" : $" ({backup})")}, to disk files, tape, or a third-party backup API (as each dump command says).");
        survey.Fact(SurveyTopics.BackupData, "Each database dump holds the whole database; a transaction log dump holds the changes since the previous dump.");
        survey.Fact(SurveyTopics.BackupRestore, "ASE loads one database at a time from its dumps (load database, then load transaction for the logs, up to a point in time), without restoring the others. Single tables aren't restored on their own.");
        survey.Fact(SurveyTopics.BackupLabels, "Each dump file's header records its server, database, type and time (load database ... with headeronly shows it).");
    }

    private static string WritersSql(string database)
    {
        var db = $"[{database.Replace("]", "]]", StringComparison.Ordinal)}]";
        var literal = $"'{database.Replace("'", "''", StringComparison.Ordinal)}'";
        return $"""
            SELECT {literal} "Database",
                   CASE p.action WHEN 195 THEN 'INSERT' WHEN 196 THEN 'DELETE' WHEN 197 THEN 'UPDATE' WHEN 198 THEN 'CREATE TABLE' ELSE convert(varchar(10), p.action) END
                     + ' (' + convert(varchar(10), count(*)) + ' grants)' "Right",
                   u.name "Grantee",
                   CASE WHEN u.uid = 0 THEN 'everyone' WHEN u.uid >= 16384 THEN 'group or role' ELSE 'user' END "Kind"
            FROM {db}..sysprotects p JOIN {db}..sysusers u ON u.uid = p.uid
            WHERE p.protecttype IN (0, 1) AND p.action IN (195, 196, 197, 198) AND u.name <> 'dbo'
            GROUP BY p.action, u.name, u.uid
            UNION ALL
            SELECT {literal}, 'acts as ' + isnull(u.name, 'another user'), l.name, 'login (alias)'
            FROM {db}..sysalternates a
            JOIN master..syslogins l ON l.suid = a.suid
            LEFT JOIN {db}..sysusers u ON u.suid = a.altsuid
            """;
    }

    private static string Stamp(string expression) => $"str_replace(convert(varchar(19), {expression}, 23), 'T', ' ')";
}
