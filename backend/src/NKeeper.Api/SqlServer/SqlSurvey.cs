using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using NKeeper.Api.Databases;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// SQL Server's answers for the insurance questionnaire: the server and its databases, who can change data
/// (server roles, database roles and grants), auditing and change history, SQL Server Agent's backup and
/// clean-up jobs with their schedules, and the backup history msdb keeps (types, how often, media, where, and
/// the software that wrote them). Only reads; a login without VIEW SERVER STATE or msdb access gets what it can see.
/// </summary>
internal static partial class SqlSurvey
{
    /// <summary>Databases whose writers and history are read one by one: past this, the rest are left out (and said so).</summary>
    private const int MaxDatabases = 60;

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb", "distribution", "SSISDB", "ReportServerTempDB" };

    public static async Task<DatabaseSurvey> RunAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var survey = new SurveyBuilder(SqlServerDialect.Instance, connection, cancellationToken);
        const string Product = "Microsoft SQL Server";

        // ---- The server ----
        var server = await survey.SectionAsync(
            "Server",
            "SERVERPROPERTY",
            """
            SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) [Server],
                   CAST(SERVERPROPERTY('MachineName') AS nvarchar(256)) [Machine],
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) [Version],
                   CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(64)) [Level],
                   CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(64)) [Update],
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) [Edition],
                   CASE WHEN SERVERPROPERTY('IsClustered') = 1 THEN 'yes' ELSE 'no' END [Failover cluster],
                   CASE WHEN SERVERPROPERTY('IsHadrEnabled') = 1 THEN 'yes' ELSE 'no' END [Always On],
                   CASE WHEN SERVERPROPERTY('IsIntegratedSecurityOnly') = 1 THEN 'Windows only' ELSE 'Windows and SQL logins' END [Authentication],
                   CAST(SERVERPROPERTY('Collation') AS nvarchar(128)) [Collation]
            """);
        string? version = null;
        foreach (var (columns, row) in SurveyBuilder.Rows(server))
        {
            var number = SurveyBuilder.Cell(columns, row, "Version");
            var name = ReleaseName(number);
            var level = string.Join(" ", new[] { SurveyBuilder.Cell(columns, row, "Level"), SurveyBuilder.Cell(columns, row, "Update") }.Where(s => s is not null and not "RTM"));
            version = $"{name ?? "SQL Server"} {SurveyBuilder.Cell(columns, row, "Edition")} {level} ({number})".Replace("  ", " ", StringComparison.Ordinal).Trim();
            survey.Fact(SurveyTopics.Software, $"{Product} {version}. Commercial software licensed from Microsoft (the database server software; who developed the applications and their databases isn't something the server can tell).");
            survey.Fact(SurveyTopics.Software, $"Instance {SurveyBuilder.Cell(columns, row, "Server")} on machine {SurveyBuilder.Cell(columns, row, "Machine")}; failover cluster: {SurveyBuilder.Cell(columns, row, "Failover cluster")}; Always On: {SurveyBuilder.Cell(columns, row, "Always On")}; sign-in: {SurveyBuilder.Cell(columns, row, "Authentication")}.");
        }

        // ---- Databases ----
        var databaseSection = await survey.SectionAsync(
            "Databases",
            "sys.databases, sys.master_files",
            DatabasesSql(encryption: true),
            DatabasesSql(encryption: false));
        var databases = new List<SurveyDatabase>();
        foreach (var (columns, row) in SurveyBuilder.Rows(databaseSection))
        {
            var name = SurveyBuilder.Cell(columns, row, "Database") ?? "?";
            var dataFile = SurveyBuilder.Cell(columns, row, "Data file");
            var size = (SurveyBuilder.Number(columns, row, "Data MB") ?? 0) + (SurveyBuilder.Number(columns, row, "Log MB") ?? 0);
            var system = SystemDatabases.Contains(name);
            databases.Add(new SurveyDatabase(
                name,
                size,
                SurveyBuilder.Cell(columns, row, "Created"),
                SurveyBuilder.Cell(columns, row, "Owner"),
                SurveyBuilder.Cell(columns, row, "State"),
                dataFile is null ? null : SurveyBuilder.Folder(dataFile),
                system));
            if (system)
            {
                continue;
            }

            survey.Fact(
                SurveyTopics.Contents,
                $"{name}: {size:N0} MB, created {SurveyBuilder.Cell(columns, row, "Created") ?? "at an unknown date"}, owner {SurveyBuilder.Cell(columns, row, "Owner") ?? "unknown"}, {SurveyBuilder.Cell(columns, row, "State")?.ToLowerInvariant()}, recovery model {SurveyBuilder.Cell(columns, row, "Recovery model")}.",
                name);
            if (dataFile is not null)
            {
                survey.Fact(SurveyTopics.Location, $"Data files in {SurveyBuilder.Folder(dataFile)}{(SurveyBuilder.Cell(columns, row, "Log file") is { } log ? $"; log in {SurveyBuilder.Folder(log)}" : "")}.", name);
            }

            if (SurveyBuilder.Cell(columns, row, "Encrypted (TDE)") == "yes")
            {
                survey.Fact(SurveyTopics.Location, "Encrypted at rest (Transparent Data Encryption).", name);
            }

            if (SurveyBuilder.Cell(columns, row, "Recovery model") == "SIMPLE")
            {
                survey.Fact(SurveyTopics.ChangeLog, "SIMPLE recovery: the transaction log is reused after each checkpoint, so changes can't be replayed or read back from it.", name);
            }
        }

        var userDatabases = databases
            .Where(d => !d.System && string.Equals(d.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToList();
        var surveyed = userDatabases.Take(MaxDatabases).ToList();
        if (userDatabases.Count > MaxDatabases)
        {
            survey.Fact(SurveyTopics.Writers, $"Only the first {MaxDatabases} of {userDatabases.Count} databases were checked for who can change data.");
        }

        // ---- Who can change data ----
        var serverRoles = await survey.SectionAsync(
            "Server roles that can change any data",
            "sys.server_role_members, sys.server_permissions",
            """
            SELECT r.name [Role], m.name [Member], m.type_desc [Kind], CASE WHEN m.is_disabled = 1 THEN 'yes' ELSE 'no' END [Disabled]
            FROM sys.server_role_members rm
            JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
            JOIN sys.server_principals m ON m.principal_id = rm.member_principal_id
            WHERE r.name IN ('sysadmin', 'serveradmin', 'securityadmin', 'dbcreator') AND m.name NOT LIKE '##%'
            UNION ALL
            SELECT 'CONTROL SERVER (granted)', m.name, m.type_desc, CASE WHEN m.is_disabled = 1 THEN 'yes' ELSE 'no' END
            FROM sys.server_permissions p JOIN sys.server_principals m ON m.principal_id = p.grantee_principal_id
            WHERE p.permission_name = 'CONTROL SERVER' AND p.state IN ('G', 'W') AND m.name NOT LIKE '##%'
            ORDER BY 1, 2
            """,
            note: "sysadmin and CONTROL SERVER can read and change everything on the server. securityadmin can grant itself more.");
        var admins = SurveyBuilder.Rows(serverRoles)
            .Where(r => SurveyBuilder.Cell(r.Columns, r.Row, "Role") is "sysadmin" or "CONTROL SERVER (granted)" && SurveyBuilder.Cell(r.Columns, r.Row, "Disabled") != "yes")
            .Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Member")} ({Kind(SurveyBuilder.Cell(r.Columns, r.Row, "Kind"))})")
            .ToList();
        if (admins.Count > 0)
        {
            survey.Fact(SurveyTopics.Writers, $"Server administrators (sysadmin), who can add, change and delete data in every database: {SurveyBuilder.List(admins, 20)}.");
        }

        var writers = await survey.MergedSectionAsync(
            "Database roles and grants that can change data",
            "sys.database_role_members, sys.database_permissions (each database)",
            surveyed.Select(db => (db, WritersSql(db), (string?)null)),
            note: "db_owner can do anything in its database; db_datawriter can add, change and delete rows in every table; db_ddladmin can change and drop tables. Grants are counted per kind of object.");
        foreach (var group in SurveyBuilder.Rows(writers).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Database") ?? "?", StringComparer.OrdinalIgnoreCase))
        {
            var parts = group
                .GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Role or permission") ?? "?", StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}: {SurveyBuilder.List(g.Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Member")} ({Kind(SurveyBuilder.Cell(r.Columns, r.Row, "Kind"))})"), 15)}");
            survey.Fact(SurveyTopics.Writers, $"{string.Join("; ", parts)}. The database owner (dbo) and server administrators can also change everything.", group.Key);
        }

        foreach (var db in surveyed.Where(db => !SurveyBuilder.Rows(writers).Any(r => string.Equals(SurveyBuilder.Cell(r.Columns, r.Row, "Database"), db, StringComparison.OrdinalIgnoreCase))))
        {
            if (writers.Note?.Contains(db, StringComparison.OrdinalIgnoreCase) != true)
            {
                survey.Fact(SurveyTopics.Writers, "No roles or grants beyond the database owner (dbo) and server administrators.", db);
            }
        }

        // ---- Auditing and change history ----
        var serverAudits = await survey.SectionAsync("SQL Server Audit", "sys.server_audits, sys.server_audit_specifications", AuditsSql);
        var audits = SurveyBuilder.Rows(serverAudits).Where(r => SurveyBuilder.Cell(r.Columns, r.Row, "Kind") == "Audit").ToList();
        survey.Fact(
            SurveyTopics.ChangeLog,
            serverAudits.Error is not null
                ? "The server's audits couldn't be read with this login (it needs VIEW ANY DEFINITION or ALTER ANY SERVER AUDIT)."
                : audits.Count == 0
                    ? "No SQL Server Audit is set up on the server."
                    : $"SQL Server Audit: {SurveyBuilder.List(audits.Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Name")} ({SurveyBuilder.Cell(r.Columns, r.Row, "State")}, to {SurveyBuilder.Cell(r.Columns, r.Row, "Detail")?.ToLowerInvariant()})"))}.");

        var auditing = await survey.SectionAsync("Audit settings and change tracking", "sys.configurations, sys.databases, sys.change_tracking_databases", ChangeTrackingSql);
        foreach (var (columns, row) in SurveyBuilder.Rows(auditing))
        {
            switch (SurveyBuilder.Cell(columns, row, "Kind"))
            {
                case "Setting" when SurveyBuilder.Cell(columns, row, "Name") == "c2 audit mode" && SurveyBuilder.Cell(columns, row, "Detail") == "1":
                    survey.Fact(SurveyTopics.ChangeLog, "C2 audit mode is on: every statement is traced to files in the data folder.");
                    break;
                case "Change data capture":
                    survey.Fact(SurveyTopics.ChangeLog, "Change data capture is on: inserts, updates and deletes of tracked tables are kept in change tables (by default for 3 days).", SurveyBuilder.Cell(columns, row, "Name"));
                    break;
                case "Change tracking":
                    survey.Fact(SurveyTopics.ChangeLog, $"Change tracking is on ({SurveyBuilder.Cell(columns, row, "Detail")}, {SurveyBuilder.Cell(columns, row, "State")}): which rows changed is kept, not their old values.", SurveyBuilder.Cell(columns, row, "Name"));
                    break;
            }
        }

        var history = await survey.MergedSectionAsync(
            "Change history kept in each database",
            "sys.tables, sys.triggers (each database)",
            surveyed.Select(db => (db, HistorySql(db, temporal: true), (string?)HistorySql(db, temporal: false))),
            note: "System-versioned (temporal) tables keep every earlier version of their rows. Triggers and tables named like audit/history often keep a change log written by the application.");
        foreach (var (columns, row) in SurveyBuilder.Rows(history))
        {
            var db = SurveyBuilder.Cell(columns, row, "Database");
            var temporal = SurveyBuilder.Number(columns, row, "System-versioned tables") ?? 0;
            var triggers = SurveyBuilder.Number(columns, row, "Table triggers") ?? 0;
            var named = SurveyBuilder.Number(columns, row, "Audit or history tables") ?? 0;
            var parts = new List<string>();
            if (temporal > 0)
            {
                parts.Add($"{SurveyBuilder.Plural((long)temporal, "system-versioned table")} keeping every earlier version of their rows");
            }

            if (named > 0)
            {
                parts.Add($"{SurveyBuilder.Plural((long)named, "table")} named like an audit or history log ({SurveyBuilder.Cell(columns, row, "Examples")})");
            }

            if (triggers > 0)
            {
                parts.Add($"{SurveyBuilder.Plural((long)triggers, "table trigger")} (often used to log changes)");
            }

            survey.Fact(
                SurveyTopics.ChangeLog,
                parts.Count == 0 ? "No change history found in the database itself (no temporal tables, triggers or audit tables)." : $"{string.Join("; ", parts)}.",
                db);
        }

        // ---- SQL Server Agent: backup and clean-up jobs ----
        var (jobs, jobsReadable) = await ReadJobsAsync(survey);
        var backupJobs = jobs.Where(j => j.IsBackup).ToList();
        var cleanupJobs = jobs.Where(j => j.IsCleanup).ToList();
        string[] jobColumns = ["Job", "Enabled", "Schedule", "Last run", "Last outcome", "What it runs"];
        if (jobs.Count > 0 || jobsReadable)
        {
            survey.Derived("Backup jobs (SQL Server Agent)", "msdb.dbo.sysjobs, sysjobsteps, sysschedules", jobColumns, backupJobs.Select(j => j.Row()));
            survey.Derived(
                "Clean-up and deletion jobs (SQL Server Agent)",
                "msdb.dbo.sysjobs, sysjobsteps, sysschedules",
                jobColumns,
                cleanupJobs.Select(j => j.Row()),
                note: "Jobs whose steps delete, purge or truncate data, or clean up old backups and history.");
            survey.Derived("Other SQL Server Agent jobs", "msdb.dbo.sysjobs", jobColumns, jobs.Where(j => !j.IsBackup && !j.IsCleanup).Select(j => j.Row()));
        }

        foreach (var job in backupJobs.Where(j => j.Enabled))
        {
            survey.Fact(SurveyTopics.BackupSchedule, $"SQL Server Agent job “{job.Name}”: {job.Schedule}{(job.LastRun is { } last ? $" (last ran {last}, {job.LastOutcome})" : "")}.");
        }

        foreach (var job in cleanupJobs.Where(j => j.Enabled))
        {
            survey.Fact(SurveyTopics.Deletion, $"SQL Server Agent job “{job.Name}”, {job.Schedule}: {SurveyBuilder.Shorten(job.Command, 220)}");
            foreach (Match match in CleanupTime().Matches(job.Command))
            {
                var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                survey.Fact(SurveyTopics.BackupRetention, $"Job “{job.Name}” deletes backup files older than {hours:0} hours ({hours / 24:0.#} days).");
            }

            if (job.Command.Contains("sp_delete_backuphistory", StringComparison.OrdinalIgnoreCase))
            {
                survey.Fact(SurveyTopics.BackupIndex, $"Job “{job.Name}” deletes old backup history from msdb.");
            }
        }

        if (!jobsReadable)
        {
            survey.Fact(SurveyTopics.BackupSchedule, "SQL Server Agent's jobs couldn't be read (Express edition has no Agent, or the login can't read msdb): backups may be scheduled by Windows Task Scheduler or backup software.");
        }
        else if (backupJobs.Count == 0)
        {
            survey.Fact(SurveyTopics.BackupSchedule, "No SQL Server Agent job runs backups: they're scheduled elsewhere (backup software or Windows Task Scheduler), if at all.");
        }

        // ---- Backup history (msdb) ----
        await BackupsAsync(survey, now, surveyed);

        survey.Fact(SurveyTopics.BackupRestore, "SQL Server backs up and restores one database at a time: any single database can be restored, or copied under another name, without restoring the others. Individual rows or tables aren't restored on their own.");
        survey.Fact(SurveyTopics.BackupData, "Each backup holds a whole database (all its tables) or, for log backups, the changes since the last one.");
        return new DatabaseSurvey(Product, version, "Microsoft (licensed)", databases, survey.Facts, survey.Sections, now);
    }

    private static async Task BackupsAsync(SurveyBuilder survey, DateTimeOffset now, IReadOnlyList<string> userDatabases)
    {
        var summary = await survey.SectionAsync(
            "Backup history by database and type",
            "msdb.dbo.backupset",
            BackupSummarySql(compressed: true),
            BackupSummarySql(compressed: false),
            note: "From the history msdb keeps of every backup; it's as old as the last time history was deleted.");
        var rows = SurveyBuilder.Rows(summary).ToList();
        if (summary.Error is not null)
        {
            survey.Fact(SurveyTopics.BackupTypes, $"The backup history in msdb couldn't be read: {SurveyBuilder.Shorten(summary.Error, 160)}");
        }
        else if (rows.Count == 0)
        {
            survey.Fact(SurveyTopics.BackupTypes, "msdb has no record of any backup: SQL Server's own backups aren't being taken (backup software may copy the server's disks instead).");
        }

        foreach (var type in rows.GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Type") ?? "?"))
        {
            var recent = type.Sum(r => SurveyBuilder.Number(r.Columns, r.Row, "Last 35 days") ?? 0);
            var dbs = type.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Database") ?? "?").ToList();
            var newest = type.Max(r => SurveyBuilder.Cell(r.Columns, r.Row, "Newest"));
            var perDb = dbs.Count > 0 ? recent / dbs.Count : 0;
            survey.Fact(
                SurveyTopics.BackupTypes,
                $"{type.Key} backups of {SurveyBuilder.Plural(dbs.Count, "database")} ({SurveyBuilder.List(dbs, 8)}); newest {newest}.");
            survey.Fact(SurveyTopics.BackupSchedule, $"{type.Key} backups, from the history: {SurveyBuilder.Frequency(perDb, 35)} per database over the last 35 days.");
        }

        foreach (var (columns, row) in rows)
        {
            var db = SurveyBuilder.Cell(columns, row, "Database");
            survey.Fact(
                SurveyTopics.BackupTypes,
                $"{SurveyBuilder.Cell(columns, row, "Type")}: {SurveyBuilder.Plural((long)(SurveyBuilder.Number(columns, row, "Backups") ?? 0), "backup")} on record since {SurveyBuilder.Cell(columns, row, "Oldest on record")}, newest {SurveyBuilder.Cell(columns, row, "Newest")}, {SurveyBuilder.Frequency(SurveyBuilder.Number(columns, row, "Last 35 days") ?? 0, 35)}.",
                db);
        }

        var backedUp = rows.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Database")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var never = userDatabases.Where(db => !backedUp.Contains(db)).ToList();
        if (never.Count > 0 && summary.Error is null)
        {
            survey.Fact(SurveyTopics.BackupTypes, $"No SQL Server backup on record for {SurveyBuilder.List(never)}.");
        }

        var oldest = rows.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Oldest on record")).Where(s => s is not null).Min();
        if (oldest is not null)
        {
            survey.Fact(SurveyTopics.BackupRetention, $"msdb's backup history goes back to {oldest}. How long the backup files themselves are kept depends on the clean-up jobs or the backup software's retention.");
        }

        survey.Fact(SurveyTopics.BackupIndex, "SQL Server records every backup in msdb (database, type, start and end, size, media and file name), which restores use to find the files they need.");

        var media = await survey.SectionAsync(
            "Backup media and destinations",
            "msdb.dbo.backupset, backupmediafamily, backupmediaset",
            MediaSql(encryption: true),
            MediaSql(encryption: false));
        var mediaRows = SurveyBuilder.Rows(media).ToList();
        foreach (var kind in mediaRows.GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Media") ?? "?"))
        {
            var software = kind.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Written by")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            survey.Fact(
                SurveyTopics.BackupMedia,
                $"{kind.Key}: {SurveyBuilder.Plural((long)kind.Sum(r => SurveyBuilder.Number(r.Columns, r.Row, "Backups") ?? 0), "backup")}, written by {(software.Count > 0 ? SurveyBuilder.List(software) : "SQL Server")}.");
            foreach (var (columns, row) in kind)
            {
                var location = SurveyBuilder.Cell(columns, row, "Location");
                if (location is null)
                {
                    continue;
                }

                survey.Fact(SurveyTopics.BackupLocation, $"{kind.Key}: {location} (newest {SurveyBuilder.Cell(columns, row, "Newest")}).");
                if (SurveyBuilder.Remote(location) is { } remote)
                {
                    survey.Fact(SurveyTopics.BackupRemote, $"Backups are written to {remote}: {location}.");
                }
            }
        }

        foreach (var software in mediaRows.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Written by")).OfType<string>().Where(s => !s.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            survey.Fact(SurveyTopics.BackupMedia, $"Backup software: {software} (it wrote backups through SQL Server's backup interface).");
        }

        if (mediaRows.Any(r => SurveyBuilder.Cell(r.Columns, r.Row, "Media")?.StartsWith("URL", StringComparison.Ordinal) == true))
        {
            survey.Fact(SurveyTopics.BackupRemote, "Some backups go straight to cloud storage (BACKUP TO URL).");
        }

        if (mediaRows.Any(r => SurveyBuilder.Cell(r.Columns, r.Row, "Encrypted") == "yes"))
        {
            survey.Fact(SurveyTopics.BackupMedia, "Some backups are encrypted by SQL Server.");
        }

        var labels = mediaRows
            .SelectMany(r => new[] { SurveyBuilder.Cell(r.Columns, r.Row, "Media set name"), SurveyBuilder.Cell(r.Columns, r.Row, "Media description") })
            .OfType<string>()
            .ToList();
        survey.Fact(
            SurveyTopics.BackupLabels,
            labels.Count > 0
                ? $"Media sets are named: {SurveyBuilder.List(labels, 8)}."
                : "Backups aren't given media names; each backup records its database and type, and the files are usually named after the database.");

        var files = await survey.SectionAsync(
            "Latest backup files",
            "msdb.dbo.backupset, backupmediafamily",
            """
            SELECT TOP 25 bs.database_name [Database],
                   CASE bs.type WHEN 'D' THEN 'Full' WHEN 'I' THEN 'Differential' WHEN 'L' THEN 'Transaction log' ELSE bs.type END [Type],
                   CONVERT(varchar(16), bs.backup_finish_date, 120) [Finished],
                   CAST(bs.backup_size / 1048576.0 AS decimal(18, 1)) [Size MB],
                   mf.physical_device_name [File or device], bs.name [Backup name], bs.user_name [Taken by]
            FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
            ORDER BY bs.backup_finish_date DESC
            """);
        var takenBy = SurveyBuilder.Rows(files).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Taken by")).OfType<string>().ToList();
        if (takenBy.Count > 0)
        {
            survey.Fact(SurveyTopics.BackupMedia, $"Recent backups were taken by the login(s) {SurveyBuilder.List(takenBy, 6)}.");
        }
    }

    // ---- Jobs ----

    private sealed record Job(string Name, bool Enabled, string Schedule, string? LastRun, string? LastOutcome, string Command, bool IsBackup, bool IsCleanup)
    {
        public string?[] Row() => [Name, Enabled ? "yes" : "no", Schedule, LastRun, LastOutcome, SurveyBuilder.Shorten(Command, 300)];
    }

    /// <returns>The jobs, and whether msdb's job tables could be read at all.</returns>
    private static async Task<(List<Job> Jobs, bool Readable)> ReadJobsAsync(SurveyBuilder survey)
    {
        var section = await survey.SectionAsync(
            "SQL Server Agent jobs",
            "msdb.dbo.sysjobs",
            """
            SELECT j.name [Job], j.enabled [Enabled], s.step_id [Step], s.step_name [Step name], s.subsystem [Subsystem],
                   LEFT(REPLACE(REPLACE(s.command, CHAR(13), ' '), CHAR(10), ' '), 1500) [Command],
                   sc.enabled [Schedule enabled], sc.freq_type [freq_type], sc.freq_interval [freq_interval],
                   sc.freq_subday_type [freq_subday_type], sc.freq_subday_interval [freq_subday_interval],
                   sc.freq_relative_interval [freq_relative_interval], sc.freq_recurrence_factor [freq_recurrence_factor],
                   sc.active_start_time [active_start_time],
                   (SELECT CONVERT(varchar(16), MAX(msdb.dbo.agent_datetime(h.run_date, h.run_time)), 120)
                    FROM msdb.dbo.sysjobhistory h WHERE h.job_id = j.job_id AND h.step_id = 0) [Last run],
                   (SELECT TOP 1 CASE h.run_status WHEN 0 THEN 'failed' WHEN 1 THEN 'succeeded' WHEN 2 THEN 'retried' WHEN 3 THEN 'cancelled' ELSE 'running' END
                    FROM msdb.dbo.sysjobhistory h WHERE h.job_id = j.job_id AND h.step_id = 0 ORDER BY h.instance_id DESC) [Last outcome]
            FROM msdb.dbo.sysjobs j
            JOIN msdb.dbo.sysjobsteps s ON s.job_id = j.job_id
            LEFT JOIN msdb.dbo.sysjobschedules js ON js.job_id = j.job_id
            LEFT JOIN msdb.dbo.sysschedules sc ON sc.schedule_id = js.schedule_id
            ORDER BY j.name, s.step_id
            """,
            appendix: false);
        var jobs = new List<Job>();
        foreach (var job in SurveyBuilder.Rows(section).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Job") ?? "?"))
        {
            var first = job.First();
            var commands = job
                .GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Step"))
                .Select(step => step.First())
                .Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Step name")}: {SurveyBuilder.Cell(r.Columns, r.Row, "Command")}")
                .ToList();
            var schedules = job
                .Where(r => SurveyBuilder.Cell(r.Columns, r.Row, "freq_type") is not null)
                .Select(r => (Enabled: SurveyBuilder.Cell(r.Columns, r.Row, "Schedule enabled") == "1", Text: DescribeSchedule(
                    Int(r, "freq_type"), Int(r, "freq_interval"), Int(r, "freq_subday_type"), Int(r, "freq_subday_interval"),
                    Int(r, "freq_relative_interval"), Int(r, "freq_recurrence_factor"), Int(r, "active_start_time"))))
                .Select(s => s.Enabled ? s.Text : $"{s.Text} (schedule disabled)")
                .Distinct()
                .ToList();
            var command = string.Join(" | ", commands);
            var text = $"{job.Key} {command}";
            var subsystems = job.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Subsystem")).ToList();
            var maintenancePlan = subsystems.Contains("SSIS") && (job.Key.Contains("maint", StringComparison.OrdinalIgnoreCase) || command.Contains("Maintenance Plans", StringComparison.OrdinalIgnoreCase));
            jobs.Add(new Job(
                job.Key,
                SurveyBuilder.Cell(first.Columns, first.Row, "Enabled") == "1",
                schedules.Count > 0 ? string.Join("; ", schedules) : "not scheduled (run by hand or by another job)",
                SurveyBuilder.Cell(first.Columns, first.Row, "Last run"),
                SurveyBuilder.Cell(first.Columns, first.Row, "Last outcome"),
                command,
                BackupWords().IsMatch(text) || maintenancePlan && job.Key.Contains("backup", StringComparison.OrdinalIgnoreCase),
                CleanupWords().IsMatch(text)));
        }

        return (jobs, section.Error is null);

        static int Int((IReadOnlyList<SqlColumn> Columns, string?[] Row) r, string name) => (int)(SurveyBuilder.Number(r.Columns, r.Row, name) ?? 0);
    }

    /// <summary>A SQL Server Agent schedule in words, from msdb.dbo.sysschedules' columns.</summary>
    internal static string DescribeSchedule(int freqType, int interval, int subdayType, int subdayInterval, int relative, int recurrence, int startTime)
    {
        var at = $"{startTime / 10000:00}:{startTime / 100 % 100:00}";
        var times = subdayType switch
        {
            2 => $"every {subdayInterval} seconds from {at}",
            4 => $"every {subdayInterval} minutes from {at}",
            8 => $"every {subdayInterval} hours from {at}",
            _ => $"at {at}",
        };
        string[] dayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
        var every = recurrence > 1 ? $"every {recurrence} " : "every ";
        switch (freqType)
        {
            case 1:
                return $"once, {times}";
            case 4:
                return $"{(interval > 1 ? $"every {interval} days" : "daily")}, {times}";
            case 8:
                var days = dayNames.Where((_, i) => (interval & (1 << i)) != 0).ToList();
                var dayText = days.Count == 7 ? "every day" : days.Count == 5 && (interval & 0b1000001) == 0 ? "weekdays" : string.Join(", ", days);
                return $"{(recurrence > 1 ? $"every {recurrence} weeks" : "weekly")} on {dayText}, {times}";
            case 16:
                return $"{every}{(recurrence > 1 ? "months" : "month")} on day {interval}, {times}";
            case 32:
                var which = relative switch { 1 => "first", 2 => "second", 4 => "third", 8 => "fourth", 16 => "last", _ => "?" };
                var day = interval switch { >= 1 and <= 7 => dayNames[interval - 1], 8 => "day", 9 => "weekday", 10 => "weekend day", _ => "?" };
                return $"{every}{(recurrence > 1 ? "months" : "month")} on the {which} {day}, {times}";
            case 64:
                return "when SQL Server Agent starts";
            case 128:
                return "when the computer is idle";
            default:
                return "on a schedule";
        }
    }

    [GeneratedRegex(@"\bBACKUP\s+(DATABASE|LOG)\b|DatabaseBackup|xp_backup|sqlbackup|Back Up Database", RegexOptions.IgnoreCase)]
    private static partial Regex BackupWords();

    [GeneratedRegex(@"\bDELETE\s+(FROM\s+)?\[?\w|\bTRUNCATE\s+TABLE\b|\bpurge|sp_delete_backuphistory|sp_purge_jobhistory|sysmail_delete|xp_delete_file|@CleanupTime\s*=\s*\d|\bclean\s*-?up\b|\bretention\b|\barchiv", RegexOptions.IgnoreCase)]
    private static partial Regex CleanupWords();

    [GeneratedRegex(@"@CleanupTime\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CleanupTime();

    // ---- SQL ----

    private static string DatabasesSql(bool encryption) => $"""
        SELECT d.name [Database], d.state_desc [State], d.recovery_model_desc [Recovery model],
               CONVERT(varchar(10), d.create_date, 120) [Created], SUSER_SNAME(d.owner_sid) [Owner],
               CAST(SUM(CASE WHEN f.type = 0 THEN f.size ELSE 0 END) * 8 / 1024.0 AS decimal(18, 1)) [Data MB],
               CAST(SUM(CASE WHEN f.type = 1 THEN f.size ELSE 0 END) * 8 / 1024.0 AS decimal(18, 1)) [Log MB],
               d.compatibility_level [Compatibility],
               {(encryption ? "CASE WHEN d.is_encrypted = 1 THEN 'yes' ELSE 'no' END" : "NULL")} [Encrypted (TDE)],
               MIN(CASE WHEN f.type = 0 THEN f.physical_name END) [Data file],
               MIN(CASE WHEN f.type = 1 THEN f.physical_name END) [Log file]
        FROM sys.databases d LEFT JOIN sys.master_files f ON f.database_id = d.database_id
        GROUP BY d.name, d.database_id, d.state_desc, d.recovery_model_desc, d.create_date, d.owner_sid, d.compatibility_level{(encryption ? ", d.is_encrypted" : "")}
        ORDER BY CASE WHEN d.database_id <= 4 THEN 0 ELSE 1 END, d.name
        """;

    private static string WritersSql(string database)
    {
        var db = Quote(database);
        var literal = Literal(database);
        return $"""
            SELECT {literal} [Database], r.name [Role or permission], m.name [Member], m.type_desc [Kind], 1 [Objects]
            FROM {db}.sys.database_role_members rm
            JOIN {db}.sys.database_principals r ON r.principal_id = rm.role_principal_id
            JOIN {db}.sys.database_principals m ON m.principal_id = rm.member_principal_id
            WHERE r.name IN ('db_owner', 'db_datawriter', 'db_ddladmin') AND m.name <> 'dbo'
            UNION ALL
            SELECT {literal}, p.permission_name + CASE p.class WHEN 0 THEN ' (whole database)' WHEN 3 THEN ' (schema)' ELSE ' (tables and other objects)' END,
                   m.name, m.type_desc, COUNT(*)
            FROM {db}.sys.database_permissions p
            JOIN {db}.sys.database_principals m ON m.principal_id = p.grantee_principal_id
            WHERE p.state IN ('G', 'W') AND p.permission_name IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'CONTROL', 'TAKE OWNERSHIP') AND m.name <> 'dbo'
            GROUP BY p.permission_name, p.class, m.name, m.type_desc
            """;
    }

    private static string HistorySql(string database, bool temporal)
    {
        var db = Quote(database);
        return $"""
            SELECT {Literal(database)} [Database],
                   {(temporal ? $"(SELECT COUNT(*) FROM {db}.sys.tables WHERE temporal_type = 2)" : "0")} [System-versioned tables],
                   (SELECT COUNT(*) FROM {db}.sys.triggers WHERE parent_class = 1 AND is_disabled = 0) [Table triggers],
                   (SELECT COUNT(*) FROM {db}.sys.tables WHERE name LIKE '%audit%' OR name LIKE '%history%' OR name LIKE '%changelog%' OR name LIKE '%change[_]log%') [Audit or history tables],
                   (SELECT STUFF((SELECT TOP 5 ', ' + name FROM {db}.sys.tables
                                  WHERE name LIKE '%audit%' OR name LIKE '%history%' OR name LIKE '%changelog%' OR name LIKE '%change[_]log%'
                                  ORDER BY name FOR XML PATH('')), 1, 2, '')) [Examples],
                   (SELECT COUNT(*) FROM {db}.sys.tables) [Tables]
            """;
    }

    private const string AuditsSql = """
        SELECT 'Audit' [Kind], a.name [Name], a.type_desc [Detail], CASE WHEN a.is_state_enabled = 1 THEN 'on' ELSE 'off' END [State], CONVERT(varchar(10), a.create_date, 120) [Created]
        FROM sys.server_audits a
        UNION ALL
        SELECT 'Server audit specification', s.name, a.name, CASE WHEN s.is_state_enabled = 1 THEN 'on' ELSE 'off' END, CONVERT(varchar(10), s.create_date, 120)
        FROM sys.server_audit_specifications s LEFT JOIN sys.server_audits a ON a.audit_guid = s.audit_guid
        """;

    private const string ChangeTrackingSql = """
        SELECT 'Setting' [Kind], c.name [Name], CONVERT(varchar(20), c.value_in_use) [Detail], NULL [State]
        FROM sys.configurations c WHERE c.name IN ('c2 audit mode', 'default trace enabled', 'common criteria compliance enabled')
        UNION ALL
        SELECT 'Change data capture', name, NULL, 'on' FROM sys.databases WHERE is_cdc_enabled = 1
        UNION ALL
        SELECT 'Change tracking', DB_NAME(database_id), CONVERT(varchar(10), retention_period) + ' ' + LOWER(retention_period_units_desc) + ' retention',
               CASE WHEN is_auto_cleanup_on = 1 THEN 'cleaned up automatically' ELSE 'kept' END
        FROM sys.change_tracking_databases
        """;

    private static string BackupSummarySql(bool compressed) => $"""
        SELECT bs.database_name [Database],
               CASE bs.type WHEN 'D' THEN 'Full' WHEN 'I' THEN 'Differential' WHEN 'L' THEN 'Transaction log' WHEN 'F' THEN 'File or filegroup'
                            WHEN 'G' THEN 'Differential file' WHEN 'P' THEN 'Partial' WHEN 'Q' THEN 'Differential partial' ELSE bs.type END [Type],
               COUNT(*) [Backups],
               CONVERT(varchar(16), MIN(bs.backup_finish_date), 120) [Oldest on record],
               CONVERT(varchar(16), MAX(bs.backup_finish_date), 120) [Newest],
               SUM(CASE WHEN bs.backup_finish_date >= DATEADD(day, -35, GETDATE()) THEN 1 ELSE 0 END) [Last 35 days],
               CAST(AVG(bs.backup_size) / 1048576.0 AS decimal(18, 1)) [Average MB],
               SUM(CASE WHEN bs.is_copy_only = 1 THEN 1 ELSE 0 END) [Copy-only],
               {(compressed ? "SUM(CASE WHEN bs.compressed_backup_size < bs.backup_size THEN 1 ELSE 0 END)" : "NULL")} [Compressed]
        FROM msdb.dbo.backupset bs
        GROUP BY bs.database_name, bs.type
        ORDER BY bs.database_name, MIN(bs.type)
        """;

    private static string MediaSql(bool encryption) => $"""
        SELECT x.[Media], x.[Written by], x.[Location], x.[Media set name], x.[Media description],
               COUNT(DISTINCT x.backup_set_id) [Backups], CONVERT(varchar(16), MIN(x.backup_finish_date), 120) [Oldest],
               CONVERT(varchar(16), MAX(x.backup_finish_date), 120) [Newest], MAX(x.[Encrypted]) [Encrypted]
        FROM (
            SELECT bs.backup_set_id, bs.backup_finish_date,
                   CASE mf.device_type WHEN 2 THEN 'Disk' WHEN 102 THEN 'Disk (backup device)' WHEN 5 THEN 'Tape' WHEN 105 THEN 'Tape (backup device)'
                                       WHEN 6 THEN 'Pipe' WHEN 7 THEN 'Virtual device (backup software)' WHEN 9 THEN 'URL (cloud storage)'
                                       ELSE 'Device type ' + CONVERT(varchar(10), mf.device_type) END [Media],
                   NULLIF(ms.software_name, '') [Written by],
                   CASE WHEN mf.device_type = 7 THEN NULLIF(mf.logical_device_name, '')
                        ELSE LEFT(mf.physical_device_name, LEN(mf.physical_device_name) - CHARINDEX('\', REVERSE(REPLACE(mf.physical_device_name, '/', '\')))) END [Location],
                   NULLIF(ms.name, '') [Media set name], NULLIF(ms.description, '') [Media description],
                   {(encryption ? "CASE WHEN bs.key_algorithm IS NOT NULL THEN 'yes' ELSE 'no' END" : "'no'")} [Encrypted]
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
            JOIN msdb.dbo.backupmediaset ms ON ms.media_set_id = bs.media_set_id
        ) x
        GROUP BY x.[Media], x.[Written by], x.[Location], x.[Media set name], x.[Media description]
        ORDER BY MAX(x.backup_finish_date) DESC
        """;

    private static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Literal(string text) => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Kind(string? typeDesc) => typeDesc switch
    {
        "WINDOWS_GROUP" => "Windows group",
        "WINDOWS_LOGIN" or "WINDOWS_USER" => "Windows account",
        "SQL_LOGIN" or "SQL_USER" => "SQL login",
        "DATABASE_ROLE" => "role",
        "SERVER_ROLE" => "server role",
        "EXTERNAL_USER" or "EXTERNAL_LOGIN" => "Entra ID account",
        "EXTERNAL_GROUP" or "EXTERNAL_GROUPS" => "Entra ID group",
        "CERTIFICATE_MAPPED_USER" or "CERTIFICATE_MAPPED_LOGIN" or "ASYMMETRIC_KEY_MAPPED_USER" or "ASYMMETRIC_KEY_MAPPED_LOGIN" => "certificate",
        null => "?",
        _ => typeDesc.ToLowerInvariant().Replace('_', ' '),
    };

    /// <summary>"SQL Server 2019" from a product version such as 15.0.4355.3.</summary>
    internal static string? ReleaseName(string? productVersion)
    {
        var parts = productVersion?.Split('.');
        if (parts is not { Length: >= 2 } || !int.TryParse(parts[0], out var major))
        {
            return null;
        }

        return major switch
        {
            17 => "SQL Server 2025",
            16 => "SQL Server 2022",
            15 => "SQL Server 2019",
            14 => "SQL Server 2017",
            13 => "SQL Server 2016",
            12 => "SQL Server 2014",
            11 => "SQL Server 2012",
            10 => parts[1].StartsWith('5') ? "SQL Server 2008 R2" : "SQL Server 2008",
            9 => "SQL Server 2005",
            _ => $"SQL Server (version {major})",
        };
    }
}
