using System.Data.Common;
using System.Text.RegularExpressions;
using NKeeper.Api.Databases;

namespace NKeeper.Api.Oracle;

/// <summary>
/// Oracle's answers for the insurance questionnaire: the database and its application schemas (the "databases"
/// a business thinks of), who can change data (DBA, ANY privileges, object grants), auditing and change history,
/// scheduler jobs that back up or delete, and RMAN's backup history, configuration and media. It reads the DBA_
/// and V$ views, so the login needs SELECT_CATALOG_ROLE (or SELECT ANY DICTIONARY); what it can't read is said.
/// </summary>
internal static partial class OracleSurvey
{
    /// <summary>Oracle's own schemas, for 11g (which has no ORACLE_MAINTAINED column).</summary>
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYS", "SYSTEM", "OUTLN", "DBSNMP", "APPQOSSYS", "AUDSYS", "CTXSYS", "DVSYS", "DVF", "GSMADMIN_INTERNAL", "GSMCATUSER", "GSMUSER",
        "LBACSYS", "MDSYS", "OJVMSYS", "OLAPSYS", "ORDDATA", "ORDPLUGINS", "ORDSYS", "SI_INFORMTN_SCHEMA", "WMSYS", "XDB", "XS$NULL",
        "ANONYMOUS", "APEX_PUBLIC_USER", "FLOWS_FILES", "MDDATA", "ORACLE_OCM", "SPATIAL_CSW_ADMIN_USR", "SPATIAL_WFS_ADMIN_USR",
        "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "REMOTE_SCHEDULER_AGENT", "DIP", "EXFSYS", "MGMT_VIEW", "OWBSYS", "OWBSYS_AUDIT",
        "SYSMAN", "SCOTT", "GGSYS", "DGPDB_INT", "PDBADMIN", "OPS$ORACLE", "DBSFWUSER", "GSMROOTUSER", "VECSYS", "BAASSYS",
    };

    public static async Task<DatabaseSurvey> RunAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var survey = new SurveyBuilder(OracleDialect.Instance, connection, cancellationToken);
        const string Product = "Oracle Database";

        // ---- The database ----
        var instance = await survey.SectionAsync(
            "Database",
            "v$instance, v$database, v$version",
            """
            SELECT i.instance_name "Instance", i.host_name "Machine", i.version "Version",
                   (SELECT MIN(banner) FROM v$version WHERE banner LIKE 'Oracle%') "Banner",
                   d.name "Database", TO_CHAR(d.created, 'YYYY-MM-DD') "Created", d.log_mode "Log mode",
                   d.flashback_on "Flashback", d.force_logging "Force logging", d.database_role "Role", d.platform_name "Platform"
            FROM v$instance i CROSS JOIN v$database d
            """);
        string? version = null;
        var archiveLog = false;
        foreach (var (columns, row) in SurveyBuilder.Rows(instance))
        {
            var banner = SurveyBuilder.Cell(columns, row, "Banner");
            version = banner is null ? SurveyBuilder.Cell(columns, row, "Version") : $"{Edition(banner)} {SurveyBuilder.Cell(columns, row, "Version")}".Trim();
            archiveLog = SurveyBuilder.Cell(columns, row, "Log mode") == "ARCHIVELOG";
            survey.Fact(SurveyTopics.Software, $"{banner ?? Product + " " + version}. Commercial software licensed from Oracle (the database server software; who developed the applications and their databases isn't something the server can tell).");
            survey.Fact(
                SurveyTopics.Software,
                $"Database {SurveyBuilder.Cell(columns, row, "Database")} (instance {SurveyBuilder.Cell(columns, row, "Instance")} on {SurveyBuilder.Cell(columns, row, "Machine")}, {SurveyBuilder.Cell(columns, row, "Platform")}), created {SurveyBuilder.Cell(columns, row, "Created")}, role {SurveyBuilder.Cell(columns, row, "Role")}.");
            survey.Fact(
                SurveyTopics.ChangeLog,
                archiveLog
                    ? "ARCHIVELOG mode: every change is written to archived redo logs, which (while they're kept) let the database be recovered to any point in time."
                    : "NOARCHIVELOG mode: redo logs are overwritten, so changes can't be replayed and only whole-database restores to a backup's time are possible.");
            if (SurveyBuilder.Cell(columns, row, "Flashback") == "YES")
            {
                survey.Fact(SurveyTopics.ChangeLog, "Flashback Database is on: the database can be wound back to a recent time.");
            }
        }

        await survey.SectionAsync(
            "Pluggable databases",
            "v$pdbs",
            "SELECT name \"Container\", open_mode \"Open mode\", TO_CHAR(creation_time, 'YYYY-MM-DD') \"Created\", ROUND(total_size / 1048576) \"Size MB\" FROM v$pdbs ORDER BY con_id",
            optional: true);

        var folders = await survey.SectionAsync(
            "Datafile folders by tablespace",
            "dba_data_files",
            "SELECT tablespace_name \"Tablespace\", MIN(file_name) \"First file\", COUNT(*) \"Files\", ROUND(SUM(bytes) / 1048576) \"Size MB\" FROM dba_data_files GROUP BY tablespace_name ORDER BY 1");
        var tablespaceFolder = SurveyBuilder.Rows(folders)
            .Where(r => SurveyBuilder.Cell(r.Columns, r.Row, "First file") is not null)
            .ToDictionary(r => SurveyBuilder.Cell(r.Columns, r.Row, "Tablespace") ?? "", r => SurveyBuilder.Folder(SurveyBuilder.Cell(r.Columns, r.Row, "First file")!), StringComparer.OrdinalIgnoreCase);

        // ---- Schemas: the business's "databases" ----
        var schemas = await survey.SectionAsync(
            "Schemas",
            "dba_users, dba_segments, dba_tables",
            SchemasSql(maintained: true),
            SchemasSql(maintained: false),
            note: "Each application keeps its tables in a schema of its own.");
        var databases = new List<SurveyDatabase>();
        foreach (var (columns, row) in SurveyBuilder.Rows(schemas))
        {
            var name = SurveyBuilder.Cell(columns, row, "Schema") ?? "?";
            var tables = SurveyBuilder.Number(columns, row, "Tables") ?? 0;
            var system = SurveyBuilder.Cell(columns, row, "Oracle's own") == "Y" || SystemSchemas.Contains(name) || name.StartsWith("APEX_", StringComparison.OrdinalIgnoreCase);
            if (tables == 0)
            {
                continue; // An account, not a store of data.
            }

            var tablespace = SurveyBuilder.Cell(columns, row, "Tablespace");
            var folder = tablespace is not null && tablespaceFolder.TryGetValue(tablespace, out var f) ? f : null;
            var size = SurveyBuilder.Number(columns, row, "Size MB");
            databases.Add(new SurveyDatabase(name, size, SurveyBuilder.Cell(columns, row, "Created"), null, SurveyBuilder.Cell(columns, row, "Status"), folder, system));
            if (!system)
            {
                survey.Fact(SurveyTopics.Contents, $"Schema {name}: {SurveyBuilder.Plural((long)tables, "table")}, {size ?? 0:N0} MB, created {SurveyBuilder.Cell(columns, row, "Created")}, account {SurveyBuilder.Cell(columns, row, "Status")?.ToLowerInvariant()}.", name);
                survey.Fact(SurveyTopics.Location, $"Tablespace {tablespace}{(folder is null ? "" : $", datafiles in {folder}")}.", name);
            }
        }

        var appSchemas = databases.Where(d => !d.System).Select(d => d.Name).ToList();

        // ---- Who can change data ----
        var powers = await survey.SectionAsync(
            "Roles and privileges that can change any data",
            "dba_role_privs, dba_sys_privs",
            """
            SELECT 'DBA role' "Right", p.grantee "Grantee", CASE WHEN r.role IS NULL THEN 'account' ELSE 'role' END "Kind"
            FROM dba_role_privs p LEFT JOIN dba_roles r ON r.role = p.grantee
            WHERE p.granted_role = 'DBA'
            UNION ALL
            SELECT s.privilege, s.grantee, CASE WHEN r.role IS NULL THEN 'account' ELSE 'role' END
            FROM dba_sys_privs s LEFT JOIN dba_roles r ON r.role = s.grantee
            WHERE s.privilege IN ('INSERT ANY TABLE', 'UPDATE ANY TABLE', 'DELETE ANY TABLE', 'ALTER ANY TABLE', 'DROP ANY TABLE', 'GRANT ANY PRIVILEGE', 'GRANT ANY OBJECT PRIVILEGE')
            ORDER BY 1, 2
            """,
            note: "These can add, change or delete rows in any schema's tables. Oracle's own roles (such as DBA, IMP_FULL_DATABASE and DATAPUMP_IMP_FULL_DATABASE) are listed too.");
        foreach (var right in SurveyBuilder.Rows(powers).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Right") ?? "?"))
        {
            var holders = right
                .Where(r => !string.Equals(SurveyBuilder.Cell(r.Columns, r.Row, "Grantee"), "SYS", StringComparison.OrdinalIgnoreCase))
                .Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Grantee")} ({SurveyBuilder.Cell(r.Columns, r.Row, "Kind")})")
                .ToList();
            if (holders.Count > 0)
            {
                survey.Fact(SurveyTopics.Writers, $"{right.Key} (any schema): {SurveyBuilder.List(holders, 15)}.");
            }
        }

        var sysdba = await survey.SectionAsync(
            "Administrators in the password file",
            "v$pwfile_users",
            "SELECT username \"Account\", sysdba \"SYSDBA\", sysoper \"SYSOPER\" FROM v$pwfile_users ORDER BY 1",
            optional: true);
        var administrators = SurveyBuilder.Rows(sysdba).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Account")).OfType<string>().ToList();
        if (administrators.Count > 0)
        {
            survey.Fact(SurveyTopics.Writers, $"Can sign in AS SYSDBA (full control of the database): {SurveyBuilder.List(administrators)}, and the server's operating-system dba group.");
        }

        var grants = await survey.SectionAsync(
            "Grants that let others change a schema's tables",
            "dba_tab_privs",
            GrantsSql(maintained: true),
            GrantsSql(maintained: false));
        foreach (var schema in SurveyBuilder.Rows(grants).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Schema") ?? "?", StringComparer.OrdinalIgnoreCase))
        {
            var parts = schema
                .GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Grantee") ?? "?")
                .Select(g => $"{g.Key} ({SurveyBuilder.List(g.Select(r => $"{SurveyBuilder.Cell(r.Columns, r.Row, "Right")} on {SurveyBuilder.Cell(r.Columns, r.Row, "Objects")}"), 4)})");
            survey.Fact(SurveyTopics.Writers, $"Granted to change its tables: {SurveyBuilder.List(parts, 15)}.", schema.Key);
        }

        foreach (var schema in appSchemas)
        {
            survey.Fact(SurveyTopics.Writers, $"The schema's own account ({schema}) can change all its tables; so can the DBAs above.", schema);
        }

        // ---- Auditing and change history ----
        var parameters = await survey.SectionAsync(
            "Audit, retention and recovery settings",
            "v$parameter, v$option",
            """
            SELECT name "Setting", value "Value" FROM v$parameter
            WHERE name IN ('audit_trail', 'audit_sys_operations', 'audit_file_dest', 'enable_ddl_logging', 'control_file_record_keep_time',
                           'db_recovery_file_dest', 'db_recovery_file_dest_size', 'recyclebin', 'undo_retention', 'db_flashback_retention_target')
            UNION ALL
            SELECT 'Unified Auditing (option)', value FROM v$option WHERE parameter = 'Unified Auditing'
            ORDER BY 1
            """);
        var settings = SurveyBuilder.Rows(parameters).ToDictionary(r => SurveyBuilder.Cell(r.Columns, r.Row, "Setting") ?? "", r => SurveyBuilder.Cell(r.Columns, r.Row, "Value"), StringComparer.OrdinalIgnoreCase);
        var unified = settings.GetValueOrDefault("Unified Auditing (option)") == "TRUE";
        if (settings.TryGetValue("audit_trail", out var trail))
        {
            var toFiles = trail?.StartsWith("OS", StringComparison.OrdinalIgnoreCase) == true || trail?.StartsWith("XML", StringComparison.OrdinalIgnoreCase) == true;
            var dest = toFiles ? settings.GetValueOrDefault("audit_file_dest") : null;
            survey.Fact(
                SurveyTopics.ChangeLog,
                trail is null || trail.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                    ? $"Traditional auditing is off (audit_trail = NONE){(unified ? "; unified auditing is available (its policies in force are listed)" : "")}."
                    : $"Auditing writes to {trail}{(dest is null ? "" : $" ({dest})")}; it records the actions chosen with AUDIT statements, not every change.");
        }

        var policies = await survey.SectionAsync(
            "Unified audit policies in force",
            "audit_unified_enabled_policies",
            "SELECT policy_name \"Policy\", enabled_option \"For\", entity_name \"Who\", success \"Success\", failure \"Failure\" FROM audit_unified_enabled_policies ORDER BY 1",
            optional: true);
        var policyNames = SurveyBuilder.Rows(policies).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Policy")).OfType<string>().ToList();
        if (policyNames.Count > 0)
        {
            survey.Fact(SurveyTopics.ChangeLog, $"Unified audit policies in force: {SurveyBuilder.List(policyNames)}.");
        }

        var fga = await survey.SectionAsync(
            "Fine-grained audit policies",
            "dba_audit_policies",
            "SELECT object_schema \"Schema\", object_name \"Table\", policy_name \"Policy\", enabled \"Enabled\", sel \"Select\", ins \"Insert\", upd \"Update\", del \"Delete\" FROM dba_audit_policies ORDER BY 1, 2",
            "SELECT object_schema \"Schema\", object_name \"Table\", policy_name \"Policy\", enabled \"Enabled\" FROM dba_audit_policies ORDER BY 1, 2",
            optional: true);
        foreach (var schema in SurveyBuilder.Rows(fga).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Schema") ?? "?"))
        {
            survey.Fact(SurveyTopics.ChangeLog, $"Fine-grained auditing on {SurveyBuilder.List(schema.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Table") ?? "?"))}.", schema.Key);
        }

        var archives = await survey.SectionAsync(
            "Flashback Data Archive (row history)",
            "dba_flashback_archive_tables, dba_flashback_archive",
            """
            SELECT t.owner_name "Schema", t.table_name "Table", t.flashback_archive_name "Archive", a.retention_in_days "Kept for days"
            FROM dba_flashback_archive_tables t JOIN dba_flashback_archive a ON a.flashback_archive_name = t.flashback_archive_name
            ORDER BY 1, 2
            """,
            optional: true);
        foreach (var schema in SurveyBuilder.Rows(archives).GroupBy(r => SurveyBuilder.Cell(r.Columns, r.Row, "Schema") ?? "?"))
        {
            survey.Fact(
                SurveyTopics.ChangeLog,
                $"Flashback Data Archive keeps every earlier version of rows of {SurveyBuilder.List(schema.Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Table") ?? "?"))} for {schema.Max(r => SurveyBuilder.Cell(r.Columns, r.Row, "Kept for days"))} days, then deletes them.",
                schema.Key);
        }

        // ---- Jobs: backups and deletions ----
        var jobs = await survey.SectionAsync(
            "Scheduler jobs",
            "dba_scheduler_jobs",
            """
            SELECT owner "Owner", job_name "Job", enabled "Enabled", state "State", job_type "Type",
                   SUBSTR(NVL(job_action, program_name), 1, 1000) "Action", NVL(repeat_interval, schedule_name) "Schedule",
                   TO_CHAR(last_start_date, 'YYYY-MM-DD HH24:MI') "Last run", TO_CHAR(next_run_date, 'YYYY-MM-DD HH24:MI') "Next run"
            FROM dba_scheduler_jobs
            ORDER BY owner, job_name
            """);
        var oldJobs = await survey.SectionAsync(
            "Jobs (DBMS_JOB)",
            "dba_jobs",
            "SELECT schema_user \"Owner\", TO_CHAR(job) \"Job\", CASE broken WHEN 'Y' THEN 'FALSE' ELSE 'TRUE' END \"Enabled\", SUBSTR(what, 1, 1000) \"Action\", interval \"Schedule\", TO_CHAR(last_date, 'YYYY-MM-DD HH24:MI') \"Last run\" FROM dba_jobs ORDER BY 1, 2",
            optional: true);
        var anyBackupJob = false;
        foreach (var (columns, row) in SurveyBuilder.Rows(jobs).Concat(SurveyBuilder.Rows(oldJobs)))
        {
            var owner = SurveyBuilder.Cell(columns, row, "Owner") ?? "";
            if (SystemSchemas.Contains(owner) || SurveyBuilder.Cell(columns, row, "Enabled") != "TRUE")
            {
                continue;
            }

            var action = SurveyBuilder.Cell(columns, row, "Action") ?? "";
            var text = $"{SurveyBuilder.Cell(columns, row, "Job")} {action}";
            var schedule = SurveyBuilder.Cell(columns, row, "Schedule") ?? "no schedule";
            if (BackupWords().IsMatch(text))
            {
                anyBackupJob = true;
                survey.Fact(SurveyTopics.BackupSchedule, $"Scheduler job {owner}.{SurveyBuilder.Cell(columns, row, "Job")}: {schedule} (last ran {SurveyBuilder.Cell(columns, row, "Last run") ?? "never"}).");
            }

            if (DeleteWords().IsMatch(text))
            {
                var fact = $"Scheduler job {owner}.{SurveyBuilder.Cell(columns, row, "Job")}, {schedule}: {SurveyBuilder.Shorten(action, 220)}";
                survey.Fact(SurveyTopics.Deletion, fact, appSchemas.Contains(owner, StringComparer.OrdinalIgnoreCase) ? owner : null);
            }
        }

        var cleanups = await survey.SectionAsync(
            "Audit trail clean-up jobs",
            "dba_audit_mgmt_cleanup_jobs",
            "SELECT job_name \"Job\", job_status \"Status\", audit_trail \"Trail\", job_frequency \"Every\" FROM dba_audit_mgmt_cleanup_jobs",
            optional: true);
        foreach (var (columns, row) in SurveyBuilder.Rows(cleanups))
        {
            survey.Fact(SurveyTopics.Deletion, $"The audit trail ({SurveyBuilder.Cell(columns, row, "Trail")?.ToLowerInvariant()}) is purged by job {SurveyBuilder.Cell(columns, row, "Job")}, {SurveyBuilder.Cell(columns, row, "Every")?.ToLowerInvariant()} ({SurveyBuilder.Cell(columns, row, "Status")?.ToLowerInvariant()}).");
        }

        if (settings.GetValueOrDefault("recyclebin") is { } recycle)
        {
            survey.Fact(SurveyTopics.Deletion, recycle.Equals("ON", StringComparison.OrdinalIgnoreCase)
                ? "Dropped tables go to the recycle bin until space is needed or it's purged."
                : "The recycle bin is off: dropped tables are gone at once.");
        }

        // ---- Backups (RMAN) ----
        await BackupsAsync(survey, settings, archiveLog, anyBackupJob);

        return new DatabaseSurvey(Product, version, "Oracle (licensed)", databases, survey.Facts, survey.Sections, now);
    }

    private static async Task BackupsAsync(SurveyBuilder survey, Dictionary<string, string?> settings, bool archiveLog, bool anyBackupJob)
    {
        var history = await survey.SectionAsync(
            "RMAN backup history by type",
            "v$rman_backup_job_details",
            """
            SELECT input_type "Type", COUNT(*) "Backups",
                   TO_CHAR(MIN(start_time), 'YYYY-MM-DD HH24:MI') "Oldest on record", TO_CHAR(MAX(end_time), 'YYYY-MM-DD HH24:MI') "Newest",
                   SUM(CASE WHEN start_time >= SYSDATE - 35 THEN 1 ELSE 0 END) "Last 35 days",
                   SUM(CASE WHEN status LIKE 'COMPLETED%' THEN 0 ELSE 1 END) "Not completed",
                   ROUND(AVG(output_bytes) / 1048576) "Average MB", MAX(output_device_type) "Device"
            FROM v$rman_backup_job_details
            GROUP BY input_type
            ORDER BY 1
            """,
            note: "The control file keeps RMAN's records for control_file_record_keep_time days (a recovery catalog, if used, keeps them longer).");
        var rows = SurveyBuilder.Rows(history).ToList();
        if (history.Error is not null)
        {
            survey.Fact(SurveyTopics.BackupTypes, $"RMAN's backup history couldn't be read: {SurveyBuilder.Shorten(history.Error, 160)}");
        }
        else if (rows.Count == 0)
        {
            survey.Fact(SurveyTopics.BackupTypes, "The control file has no record of an RMAN backup: the database may be copied another way (storage snapshots, Data Pump exports or file-level backups), or not at all.");
        }

        foreach (var (columns, row) in rows)
        {
            var type = TypeName(SurveyBuilder.Cell(columns, row, "Type"));
            survey.Fact(
                SurveyTopics.BackupTypes,
                $"{type}: {SurveyBuilder.Plural((long)(SurveyBuilder.Number(columns, row, "Backups") ?? 0), "backup")} on record since {SurveyBuilder.Cell(columns, row, "Oldest on record")}, newest {SurveyBuilder.Cell(columns, row, "Newest")}, to {SurveyBuilder.Cell(columns, row, "Device") ?? "?"}.");
            survey.Fact(SurveyTopics.BackupSchedule, $"{type}, from the history: {SurveyBuilder.Frequency(SurveyBuilder.Number(columns, row, "Last 35 days") ?? 0, 35)} over the last 35 days.");
        }

        if (!anyBackupJob)
        {
            survey.Fact(SurveyTopics.BackupSchedule, "No database scheduler job runs RMAN: backups are started by cron or a scheduler on the server, or by backup software.");
        }

        survey.Fact(
            SurveyTopics.BackupData,
            $"RMAN backs up the database's datafiles (every schema), the control file and server parameter file{(archiveLog ? ", and the archived redo logs (every change since)" : "")}.");

        var configuration = await survey.SectionAsync(
            "RMAN configuration",
            "v$rman_configuration",
            "SELECT name \"Setting\", value \"Value\" FROM v$rman_configuration ORDER BY conf#",
            note: "Settings not listed have RMAN's defaults (retention: redundancy 1; device: disk).");
        var config = SurveyBuilder.Rows(configuration).Select(r => (Name: SurveyBuilder.Cell(r.Columns, r.Row, "Setting") ?? "", Value: SurveyBuilder.Cell(r.Columns, r.Row, "Value") ?? "")).ToList();
        if (configuration.Error is null)
        {
            var retention = config.FirstOrDefault(c => c.Name.Contains("RETENTION POLICY", StringComparison.OrdinalIgnoreCase));
            survey.Fact(
                SurveyTopics.BackupRetention,
                retention.Value is { Length: > 0 }
                    ? $"RMAN retention policy: {retention.Value.ToLowerInvariant()} (older backups become obsolete and are deleted by DELETE OBSOLETE or when the recovery area needs space)."
                    : "RMAN's retention policy is the default, redundancy 1: only the latest backup is kept once DELETE OBSOLETE runs or the recovery area needs space.");
            foreach (var (name, value) in config)
            {
                if (name.Contains("CHANNEL", StringComparison.OrdinalIgnoreCase) && SbtLibrary().Match(value) is { Success: true } library)
                {
                    survey.Fact(SurveyTopics.BackupMedia, $"Media manager library: {library.Groups[1].Value} ({MediaManager(library.Groups[1].Value)}).");
                    if (library.Groups[1].Value.Contains("libopc", StringComparison.OrdinalIgnoreCase) || library.Groups[1].Value.Contains("libra", StringComparison.OrdinalIgnoreCase))
                    {
                        survey.Fact(SurveyTopics.BackupRemote, $"Backups go through {MediaManager(library.Groups[1].Value)}.");
                    }
                }

                if (name.Contains("ENCRYPTION", StringComparison.OrdinalIgnoreCase))
                {
                    survey.Fact(SurveyTopics.BackupMedia, $"RMAN encryption: {value}.");
                }

                if (name.Contains("COMPRESSION", StringComparison.OrdinalIgnoreCase))
                {
                    survey.Fact(SurveyTopics.BackupMedia, $"RMAN compression: {value}.");
                }

                if (name.Contains("DEFAULT DEVICE TYPE", StringComparison.OrdinalIgnoreCase))
                {
                    survey.Fact(SurveyTopics.BackupMedia, $"Default device: {value}.");
                }
            }
        }

        if (settings.GetValueOrDefault("control_file_record_keep_time") is { } keep)
        {
            survey.Fact(SurveyTopics.BackupIndex, $"RMAN records every backup (its files, time, type and what it holds) in the control file, kept for {keep} days at least (control_file_record_keep_time); a recovery catalog database keeps it for longer if one is used.");
        }

        var pieces = await survey.SectionAsync(
            "Backups still available, by device and location",
            "v$backup_piece",
            """
            SELECT x."Device", x."Location", COUNT(*) "Pieces", TO_CHAR(MIN(x.completion_time), 'YYYY-MM-DD HH24:MI') "Oldest",
                   TO_CHAR(MAX(x.completion_time), 'YYYY-MM-DD HH24:MI') "Newest", ROUND(SUM(x.bytes) / 1073741824, 1) "GB",
                   MAX(x.encrypted) "Encrypted", MAX(x.compressed) "Compressed"
            FROM (SELECT device_type "Device",
                         CASE WHEN device_type = 'DISK' THEN REGEXP_REPLACE(handle, '[/\][^/\]*$', '') ELSE NVL(media, '(media manager)') END "Location",
                         completion_time, bytes, encrypted, compressed
                  FROM v$backup_piece WHERE status = 'A') x
            GROUP BY x."Device", x."Location"
            ORDER BY MAX(x.completion_time) DESC
            """);
        var oldest = SurveyBuilder.Rows(pieces).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Oldest")).OfType<string>().Min();
        if (oldest is not null)
        {
            survey.Fact(SurveyTopics.BackupRetention, $"The oldest backup still available is from {oldest}: the database can be restored as far back as that.");
        }

        foreach (var (columns, row) in SurveyBuilder.Rows(pieces))
        {
            var device = SurveyBuilder.Cell(columns, row, "Device");
            var location = SurveyBuilder.Cell(columns, row, "Location");
            var media = device == "DISK" ? "Disk" : device == "SBT_TAPE" ? "Media manager (SBT: tape, backup appliance or cloud through backup software)" : device;
            survey.Fact(SurveyTopics.BackupMedia, $"{media}: {SurveyBuilder.Cell(columns, row, "Pieces")} backup pieces, {SurveyBuilder.Cell(columns, row, "GB")} GB{(SurveyBuilder.Cell(columns, row, "Encrypted") == "YES" ? ", encrypted" : "")}{(SurveyBuilder.Cell(columns, row, "Compressed") == "YES" ? ", compressed" : "")}.");
            if (location is not null)
            {
                survey.Fact(SurveyTopics.BackupLocation, $"{media}: {location} (newest {SurveyBuilder.Cell(columns, row, "Newest")}).");
                if (SurveyBuilder.Remote(location) is { } remote)
                {
                    survey.Fact(SurveyTopics.BackupRemote, $"Backups are written to {remote}: {location}.");
                }
            }

            if (device == "SBT_TAPE")
            {
                survey.Fact(SurveyTopics.BackupRemote, "Some backups go to a media manager (SBT), which stores them on tape, a backup appliance or in the cloud, as its backup software is set up.");
            }
        }

        var tags = await survey.SectionAsync(
            "Backup tags",
            "v$backup_piece",
            "SELECT tag \"Tag\", COUNT(*) \"Pieces\", TO_CHAR(MAX(completion_time), 'YYYY-MM-DD') \"Newest\" FROM v$backup_piece WHERE status = 'A' AND tag IS NOT NULL GROUP BY tag ORDER BY MAX(completion_time) DESC FETCH FIRST 30 ROWS ONLY",
            "SELECT * FROM (SELECT tag \"Tag\", COUNT(*) \"Pieces\", TO_CHAR(MAX(completion_time), 'YYYY-MM-DD') \"Newest\" FROM v$backup_piece WHERE status = 'A' AND tag IS NOT NULL GROUP BY tag ORDER BY MAX(completion_time) DESC) WHERE ROWNUM <= 30");
        var named = SurveyBuilder.Rows(tags).Select(r => SurveyBuilder.Cell(r.Columns, r.Row, "Tag")).OfType<string>().Where(t => !AutomaticTag().IsMatch(t)).ToList();
        survey.Fact(
            SurveyTopics.BackupLabels,
            named.Count > 0
                ? $"Backups are tagged: {SurveyBuilder.List(named, 10)}."
                : "Backups carry RMAN's automatic tags (TAGyyyymmddThhmmss); RMAN knows each one's contents, so they needn't be labelled.");

        var area = await survey.SectionAsync(
            "Fast recovery area",
            "v$recovery_file_dest",
            "SELECT name \"Location\", ROUND(space_limit / 1073741824, 1) \"Limit GB\", ROUND(space_used / 1073741824, 1) \"Used GB\", ROUND(space_reclaimable / 1073741824, 1) \"Reclaimable GB\", number_of_files \"Files\" FROM v$recovery_file_dest",
            optional: true);
        foreach (var (columns, row) in SurveyBuilder.Rows(area))
        {
            if (SurveyBuilder.Cell(columns, row, "Location") is { } location)
            {
                survey.Fact(SurveyTopics.BackupLocation, $"Fast recovery area: {location} ({SurveyBuilder.Cell(columns, row, "Used GB")} of {SurveyBuilder.Cell(columns, row, "Limit GB")} GB used). Backups there are deleted automatically when space is needed, once obsolete.");
            }
        }

        survey.Fact(SurveyTopics.BackupRestore, "RMAN restores the whole database, or one tablespace or datafile, to the latest time or (in ARCHIVELOG mode) to a point in time. A single schema is restored into another (auxiliary) database or table by table (RECOVER TABLE, 12c and later), without overwriting the live one.");
    }

    // ---- Wording ----

    private static string Edition(string banner)
    {
        var match = Regex.Match(banner, @"(Enterprise|Standard|Express|Personal|Free)\s*Edition|Database\s+\d+\w*\s+(Enterprise|Standard|Express|Free)", RegexOptions.IgnoreCase);
        return match.Success ? $"{(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)} Edition" : string.Empty;
    }

    private static string TypeName(string? inputType) => inputType switch
    {
        "DB FULL" => "Full database",
        "DB INCR" => "Incremental database",
        "DATAFILE FULL" => "Full datafile",
        "DATAFILE INCR" => "Incremental datafile",
        "ARCHIVELOG" => "Archived redo log",
        "CONTROLFILE" => "Control file",
        "SPFILE" => "Server parameter file",
        "RECVR AREA" => "Recovery area",
        "BACKUPSET" => "Copy of backup sets",
        null => "?",
        _ => inputType,
    };

    private static string MediaManager(string library) => library.ToLowerInvariant() switch
    {
        var l when l.Contains("libopc") => "Oracle Database Cloud Backup Module (cloud object storage)",
        var l when l.Contains("libra") => "Zero Data Loss Recovery Appliance",
        var l when l.Contains("libobk") || l.Contains("osb") => "Oracle Secure Backup",
        var l when l.Contains("nbu") || l.Contains("netbackup") => "Veritas NetBackup",
        var l when l.Contains("commvault") || l.Contains("galaxy") => "Commvault",
        var l when l.Contains("ddboost") || l.Contains("libddobk") => "Dell Data Domain Boost",
        var l when l.Contains("tdpo") || l.Contains("tsm") => "IBM Spectrum Protect (TSM)",
        var l when l.Contains("networker") || l.Contains("nsrora") => "Dell NetWorker",
        var l when l.Contains("veeam") => "Veeam Plug-in for Oracle RMAN",
        var l when l.Contains("rubrik") => "Rubrik",
        var l when l.Contains("cohesity") => "Cohesity",
        _ => "third-party backup software",
    };

    [GeneratedRegex(@"\bRMAN\b|\bBACKUP\b|expdp|DBMS_DATAPUMP", RegexOptions.IgnoreCase)]
    private static partial Regex BackupWords();

    [GeneratedRegex(@"\bDELETE\s+(FROM\s+)?\w|\bTRUNCATE\s+TABLE\b|\bPURGE\b|_PURGE|PURGE_|\bCLEAN_?UP|ARCHIV", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteWords();

    [GeneratedRegex(@"SBT_LIBRARY\s*=\s*([^,)'""\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SbtLibrary();

    [GeneratedRegex(@"^TAG\d{8}T\d{6}$", RegexOptions.IgnoreCase)]
    private static partial Regex AutomaticTag();

    // ---- SQL ----

    private static string SchemasSql(bool maintained) => $"""
        SELECT u.username "Schema", TO_CHAR(u.created, 'YYYY-MM-DD') "Created", u.account_status "Status", u.default_tablespace "Tablespace",
               ROUND(NVL(s.bytes, 0) / 1048576) "Size MB", NVL(t.tables, 0) "Tables", {(maintained ? "u.oracle_maintained" : "'?'")} "Oracle's own"
        FROM dba_users u
        LEFT JOIN (SELECT owner, SUM(bytes) bytes FROM dba_segments GROUP BY owner) s ON s.owner = u.username
        LEFT JOIN (SELECT owner, COUNT(*) tables FROM dba_tables GROUP BY owner) t ON t.owner = u.username
        ORDER BY NVL(s.bytes, 0) DESC, u.username
        """;

    private static string GrantsSql(bool maintained) => $"""
        SELECT p.owner "Schema", p.privilege "Right", p.grantee "Grantee", COUNT(*) "Objects"
        FROM dba_tab_privs p
        WHERE p.privilege IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER')
          AND {(maintained ? "p.owner IN (SELECT username FROM dba_users WHERE oracle_maintained = 'N')" : $"p.owner NOT IN ({string.Join(", ", SystemSchemas.Select(OracleDba.Literal))})")}
        GROUP BY p.owner, p.privilege, p.grantee
        ORDER BY 1, 3, 2
        """;
}
