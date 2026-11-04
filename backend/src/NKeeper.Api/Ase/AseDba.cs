using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>
/// SAP ASE's Management reports: Space (databases' data and log, devices) and Replication Drift (what each
/// replicate last applied, rs_ticket latency, the exceptions log, replicated tables), and comparing the row
/// counts of replicated tables with another server. Reports only read; scripts they offer are run by the user.
/// </summary>
internal static class AseDba
{
    public const string Space = "space";
    public const string Drift = "drift";
    public const string CopyTool = "copy";

    /// <summary>Databases that are the server's own: not offered as copies' targets, nor counted as user data.</summary>
    internal static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "tempdb", "sybsystemprocs", "sybsystemdb", "sybsecurity", "sybmgmtdb", "dbccdb", "dbccalt", "sybpcidb",
    };

    private const int MaxScripts = 20;
    private const int MaxCompareTables = 500;

    public static IReadOnlyList<SqlObjectNode> ToolNodes =>
    [
        new() { Kind = Kinds.Tool, Label = "Space", Detail = "Databases, logs and devices", Name = Space },
        new() { Kind = Kinds.Tool, Label = "Replication Drift", Detail = "Last applied, latency, exceptions", Name = Drift },
        new() { Kind = Kinds.Tool, Label = "Copy Database", Detail = "Dump here, load on another server", Name = CopyTool },
    ];

    public static Task<ToolReport> ReportAsync(DbConnection connection, string tool, DateTimeOffset now, CancellationToken cancellationToken) => tool switch
    {
        Space => SpaceAsync(connection, now, cancellationToken),
        Drift => DriftAsync(connection, now, cancellationToken),
        _ => throw new SqlExplorerException($"There's no “{tool}” report."),
    };

    // ---- Space ----

    /// <summary>A database's space, in MB: data (every fragment that isn't log-only) and log.</summary>
    internal sealed record DatabaseSpace(string Name, double DataMb, double DataFreeMb, double LogMb, double LogFreeMb, bool MixedLog);

    internal sealed record DeviceSpace(string Name, string Path, double SizeMb, double AllocatedMb)
    {
        public double FreeMb => Math.Max(0, SizeMb - AllocatedMb);
    }

    private const string PageMb = "(@@maxpagesize / 1048576.0)";

    internal static async Task<IReadOnlyList<DatabaseSpace>> DatabaseSpaceAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT d.name,
                   sum(CASE WHEN u.segmap <> 4 THEN u.size ELSE 0 END) * {PageMb},
                   sum(CASE WHEN u.segmap <> 4 THEN curunreservedpgs(u.dbid, u.lstart, u.unreservedpgs) ELSE 0 END) * {PageMb},
                   sum(CASE WHEN u.segmap = 4 THEN u.size ELSE 0 END) * {PageMb},
                   max(lct_admin('logsegment_freepages', d.dbid)) * {PageMb},
                   sum(CASE WHEN u.segmap & 4 = 4 AND u.segmap <> 4 THEN 1 ELSE 0 END)
            FROM master..sysdatabases d JOIN master..sysusages u ON u.dbid = d.dbid
            WHERE d.status2 & 16 = 0 AND d.status & 32 = 0
            GROUP BY d.name, d.dbid
            ORDER BY d.name
            """,
            [],
            r => new DatabaseSpace(
                DbQuery.Text(r, 0)!,
                Number(r, 1),
                Number(r, 2),
                Number(r, 3),
                Number(r, 4),
                DbQuery.Int(r, 5) > 0),
            cancellationToken);

    internal static async Task<IReadOnlyList<DeviceSpace>> DeviceSpaceAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT d.name, d.phyname, (d.high - d.low + 1) / 512.0,
                   isnull((SELECT sum(u.size) FROM master..sysusages u WHERE u.vdevno = d.vdevno), 0) * {PageMb}
            FROM master..sysdevices d
            WHERE d.status & 2 = 2
            ORDER BY d.name
            """,
            [],
            r => new DeviceSpace(DbQuery.Text(r, 0)!, DbQuery.Text(r, 1) ?? string.Empty, Number(r, 2), Number(r, 3)),
            cancellationToken);

    private static async Task<ToolReport> SpaceAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ToolSection>();
        IReadOnlyList<DeviceSpace> devices = [];

        try
        {
            devices = await DeviceSpaceAsync(connection, cancellationToken);
        }
        catch (DbException)
        {
            // Shown as the devices section's error below.
        }

        var holds = new Dictionary<string, (DateTime? Transaction, int? Spid, DateTime? Replication)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var hold in await DbQuery.ListAsync(
                connection,
                """
                SELECT db_name(dbid),
                       min(CASE WHEN spid <> 0 THEN starttime END), min(CASE WHEN spid <> 0 THEN spid END),
                       min(CASE WHEN name = '$replication_truncation_point' THEN starttime END)
                FROM master..syslogshold GROUP BY dbid
                """,
                [],
                r => (Db: DbQuery.Text(r, 0) ?? string.Empty, Transaction: Date(r, 1), Spid: r.IsDBNull(2) ? (int?)null : DbQuery.Int(r, 2), Replication: Date(r, 3)),
                cancellationToken))
            {
                holds[hold.Db] = (hold.Transaction, hold.Spid, hold.Replication);
            }
        }
        catch (DbException)
        {
            // Without syslogshold, the warning just doesn't say what holds the log.
        }

        try
        {
            var databases = await DatabaseSpaceAsync(connection, cancellationToken);
            var rows = new List<string?[]>();
            var scripts = new List<ToolScript>();
            foreach (var db in databases)
            {
                var dataUsed = Percent(db.DataMb - db.DataFreeMb, db.DataMb);
                var logUsed = db.MixedLog ? (double?)null : Percent(db.LogMb - db.LogFreeMb, db.LogMb);
                rows.Add(
                [
                    db.Name,
                    Mb(db.DataMb),
                    Mb(db.DataFreeMb),
                    dataUsed?.ToString("0.0", CultureInfo.InvariantCulture),
                    db.MixedLog ? "on data" : Mb(db.LogMb),
                    db.MixedLog ? null : Mb(db.LogFreeMb),
                    logUsed?.ToString("0.0", CultureInfo.InvariantCulture),
                ]);

                if (dataUsed >= 85)
                {
                    warnings.Add($"{db.Name}: data is {dataUsed:0}% full ({Mb(db.DataFreeMb)} MB free).");
                    if (scripts.Count < MaxScripts && Roomiest(devices) is { } device)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: add data space on {device.Name}", $"alter database {Quote(db.Name)} on {device.Name} = '{GrowBy(db.DataMb, device)}'"));
                    }
                }

                if (logUsed >= 80)
                {
                    var held = holds.TryGetValue(db.Name, out var h)
                        ? string.Join("; ", new[]
                        {
                            h.Transaction is { } t ? $"the oldest open transaction (spid {h.Spid}) has held it since {Stamp(t)}" : null,
                            h.Replication is { } r ? $"replication's truncation point has held it since {Stamp(r)}" : null,
                        }.Where(x => x is not null))
                        : string.Empty;
                    warnings.Add($"{db.Name}: the log is {logUsed:0}% full{(held.Length > 0 ? $": {held}" : "")}. Dump the transaction log, or end what holds it.");
                    if (scripts.Count < MaxScripts && Roomiest(devices) is { } device)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: add log space on {device.Name}", $"alter database {Quote(db.Name)} log on {device.Name} = '{GrowBy(db.LogMb, device)}'"));
                    }

                    if (scripts.Count < MaxScripts)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: dump the log", $"dump transaction {Quote(db.Name)} to '/path/to/{db.Name}_log.dmp'"));
                    }
                }

                if (db.MixedLog && dataUsed >= 85)
                {
                    warnings.Add($"{db.Name} keeps its log on its data devices: a full log and full data are the same thing there.");
                }
            }

            scripts.Add(new ToolScript("Space of one database (sp_helpdb)", "sp_helpdb dbname"));
            IReadOnlyList<SqlColumn> columns =
            [
                new("Database", "varchar", false),
                new("Data MB", "numeric", true),
                new("Data free MB", "numeric", true),
                new("Data used %", "numeric", true),
                new("Log MB", "numeric", true),
                new("Log free MB", "numeric", true),
                new("Log used %", "numeric", true),
            ];
            sections.Add(new ToolSection(
                "Databases",
                "master..sysusages, curunreservedpgs, lct_admin('logsegment_freepages')",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null,
                scripts,
                "A database whose log shares its data devices shows its log as “on data”: its data figures include the log."));
        }
        catch (DbException ex)
        {
            sections.Add(new ToolSection("Databases", "master..sysusages", [], ex.Message));
        }

        if (devices.Count > 0)
        {
            IReadOnlyList<SqlColumn> columns =
            [
                new("Device", "varchar", false),
                new("Path", "varchar", false),
                new("Size MB", "numeric", true),
                new("Allocated MB", "numeric", true),
                new("Free MB", "numeric", true),
            ];
            var rows = devices.Select(d => new[] { d.Name, d.Path, Mb(d.SizeMb), Mb(d.AllocatedMb), Mb(d.FreeMb) }).ToList<string?[]>();
            sections.Add(new ToolSection(
                "Devices",
                "master..sysdevices, master..sysusages",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null,
                [new ToolScript("New device (disk init)", "disk init name = 'data_dev2', physname = '/sybase/data/data_dev2.dat', size = '10G'")],
                "Free is space on the device not yet given to any database: alter database can use it."));
        }
        else
        {
            sections.Add(await AseReplication.SectionAsync(connection, "Devices", "sp_helpdevice", cancellationToken));
        }

        return new ToolReport(warnings, [], sections, now);
    }

    private static DeviceSpace? Roomiest(IReadOnlyList<DeviceSpace> devices) =>
        devices.Where(d => d.FreeMb >= 16 && !d.Name.Equals("master", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.FreeMb).FirstOrDefault();

    /// <summary>A quarter more (at least 256 MB), or what the device has left.</summary>
    private static string GrowBy(double currentMb, DeviceSpace device)
    {
        var mb = Math.Min(Math.Max(256, Math.Ceiling(currentMb / 4)), Math.Floor(device.FreeMb));
        return $"{mb.ToString("0", CultureInfo.InvariantCulture)}M";
    }

    // ---- Replication drift ----

    private sealed record ReplicationRoles(string Database, int ReplicatedTables, bool Replicate, bool TicketHistory, bool Rssd);

    private static async Task<ToolReport> DriftAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ToolSection>();
        var serverNow = (await DbQuery.ListAsync(connection, "SELECT getdate()", [], r => Convert.ToDateTime(r.GetValue(0), CultureInfo.InvariantCulture), cancellationToken))[0];

        var roles = new List<ReplicationRoles>();
        var names = await DbQuery.ListAsync(
            connection, "SELECT name FROM master..sysdatabases WHERE status2 & 16 = 0 AND status & 32 = 0 ORDER BY name", [], r => DbQuery.Text(r, 0)!, cancellationToken);
        foreach (var name in names.Where(n => !n.Equals("tempdb", StringComparison.OrdinalIgnoreCase) && !n.Equals("model", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var db = Quote(name);
                var found = await DbQuery.ListAsync(
                    connection,
                    $"""
                    SELECT (SELECT count(*) FROM {db}..sysobjects WHERE type = 'U' AND sysstat & -32768 = -32768),
                           (SELECT count(*) FROM {db}..sysobjects WHERE type = 'U' AND name = 'rs_lastcommit'),
                           (SELECT count(*) FROM {db}..sysobjects WHERE type = 'U' AND name = 'rs_ticket_history'),
                           (SELECT count(*) FROM {db}..sysobjects WHERE type = 'U' AND name = 'rs_exceptshdr')
                    """,
                    [],
                    r => new ReplicationRoles(name, DbQuery.Int(r, 0), DbQuery.Int(r, 1) > 0, DbQuery.Int(r, 2) > 0, DbQuery.Int(r, 3) > 0),
                    cancellationToken);
                roles.Add(found[0]);
            }
            catch (DbException)
            {
                // A database this login can't use: left out.
            }
        }

        var involved = roles.Where(r => r.ReplicatedTables > 0 || r.Replicate || r.Rssd).ToList();
        IReadOnlyList<SqlColumn> roleColumns =
        [
            new("Database", "varchar", false),
            new("Replicated tables", "int", true),
            new("Replicate (rs_lastcommit)", "varchar", false),
            new("RSSD", "varchar", false),
        ];
        sections.Add(new ToolSection(
            "Databases in replication",
            "sysobjects (replicated tables, rs_lastcommit, rs_exceptshdr)",
            [new SqlOutput("results", Columns: roleColumns, Rows: [.. involved.Select(r => new[] { r.Database, r.ReplicatedTables.ToString(CultureInfo.InvariantCulture), r.Replicate ? "yes" : null, r.Rssd ? "yes" : null })], RowCount: involved.Count)],
            null,
            null,
            involved.Count == 0 ? "No database here has replicated tables, a replicate's rs_lastcommit, or an RSSD." : "A primary has replicated tables; a replicate has rs_lastcommit, updated as Replication Server applies each transaction."));

        // What each replicate database last applied, from each primary.
        var lastRows = new List<string?[]>();
        foreach (var role in roles.Where(r => r.Replicate))
        {
            try
            {
                var applied = await DbQuery.ListAsync(
                    connection,
                    $"SELECT origin, origin_time, dest_commit_time FROM {Quote(role.Database)}..rs_lastcommit WHERE origin > 0 ORDER BY dest_commit_time DESC",
                    [],
                    r => (Origin: DbQuery.Int(r, 0), OriginTime: Date(r, 1), Applied: Date(r, 2)),
                    cancellationToken);
                foreach (var a in applied)
                {
                    var since = a.Applied is { } t ? serverNow - t : (TimeSpan?)null;
                    var latency = a.Applied is { } d && a.OriginTime is { } o ? d - o : (TimeSpan?)null;
                    lastRows.Add(
                    [
                        role.Database,
                        a.Origin.ToString(CultureInfo.InvariantCulture),
                        a.OriginTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        a.Applied?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        since is { } s ? Duration(s) : null,
                        latency is { } l ? Duration(l) : null,
                    ]);
                    if (since > TimeSpan.FromHours(1))
                    {
                        warnings.Add($"{role.Database}: the last transaction from origin {a.Origin} was applied {Duration(since.Value)} ago, at {Stamp(a.Applied)} (committed on the primary at {Stamp(a.OriginTime)}). If the primary has changed since, replication is behind or stopped.");
                    }

                    if (latency > TimeSpan.FromMinutes(5))
                    {
                        warnings.Add($"{role.Database}: the last transaction from origin {a.Origin} took {Duration(latency.Value)} to arrive (committed on the primary at {Stamp(a.OriginTime)}, applied at {Stamp(a.Applied)}).");
                    }
                }
            }
            catch (DbException ex)
            {
                warnings.Add($"{role.Database}: couldn't read rs_lastcommit ({ex.Message}).");
            }
        }

        if (lastRows.Count > 0 || roles.Any(r => r.Replicate))
        {
            IReadOnlyList<SqlColumn> columns =
            [
                new("Database", "varchar", false),
                new("Origin", "int", true),
                new("Committed on primary", "datetime", false),
                new("Applied here", "datetime", false),
                new("Since applied", "varchar", false),
                new("Latency", "varchar", false),
            ];
            sections.Add(new ToolSection(
                "Last transaction applied (replicate databases)",
                "rs_lastcommit",
                [new SqlOutput("results", Columns: columns, Rows: lastRows, RowCount: lastRows.Count)],
                null,
                null,
                "Origin is the primary's database id in the RSSD (rs_databases). A quiet primary sends nothing, so an old time alone isn't a fault: rs_ticket proves the path end to end."));
        }

        foreach (var role in roles.Where(r => r.TicketHistory))
        {
            sections.Add(await AseReplication.SectionAsync(
                connection,
                $"rs_ticket history · {role.Database}",
                $"SET ROWCOUNT 10 SELECT * FROM {Quote(role.Database)}..rs_ticket_history ORDER BY cnt DESC SET ROWCOUNT 0",
                cancellationToken) with
            {
                Scripts = [new ToolScript("Send a ticket (run on the primary database)", "exec rs_ticket 'nkeeper'")],
            });
        }

        foreach (var role in roles.Where(r => r.Rssd))
        {
            var exceptions = await AseReplication.SectionAsync(
                connection,
                $"Exceptions log · {role.Database} (RSSD)",
                $"""
                SELECT orig_site "Primary server", orig_db "Primary db", error_site "Replicate server", error_db "Replicate db",
                       count(*) "Transactions", max(log_time) "Latest"
                FROM {Quote(role.Database)}..rs_exceptshdr
                GROUP BY orig_site, orig_db, error_site, error_db
                ORDER BY max(log_time) DESC
                """,
                cancellationToken);
            var failed = ToolReports.Rows(exceptions).Sum(x => ToolReports.Number(x.Columns, x.Row, "Transactions") ?? 0);
            if (failed > 0)
            {
                // Newest first: the first row's Latest is the most recent of all.
                var latest = ToolReports.Rows(exceptions).Select(x => ToolReports.Cell(x.Columns, x.Row, "Latest")).FirstOrDefault();
                warnings.Add($"{failed:0} transaction{(failed == 1 ? " is" : "s are")} in the exceptions log ({role.Database}), the latest logged at {Trim(latest)}: skipped or failed on a replicate, so it differs from its primary.");
            }

            sections.Add(exceptions with
            {
                Scripts =
                [
                    new ToolScript("List them (run in the RSSD)", "rs_helpexception"),
                    new ToolScript("Show one in full (run in the RSSD)", "rs_helpexception transaction_id, v"),
                ],
            });
        }

        return new ToolReport(warnings, [], sections, now);
    }

    // ---- Comparing row counts with another server ----

    /// <summary>
    /// Row counts of a database's replicated tables (every user table when none is marked) here and in a database on
    /// another server. Estimated counts (row_count) are instant; exact ones run count(*) on each table.
    /// </summary>
    public static async Task<ToolReport> CompareAsync(
        DbConnection here, string hereName, string database, DbConnection there, string thereName, string otherDatabase, bool exact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var mine = await TableCountsAsync(here, database, exact, onlyNames: null, cancellationToken);
        var theirs = await TableCountsAsync(there, otherDatabase, exact, onlyNames: [.. mine.Keys], cancellationToken);
        var warnings = new List<string>();
        var rows = new List<string?[]>();
        var differ = 0;
        foreach (var (table, count) in mine.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            theirs.TryGetValue(table, out var other);
            var difference = other is null ? null : count - other;
            if (other is null || difference != 0)
            {
                differ++;
            }

            rows.Add(
            [
                table,
                count?.ToString(CultureInfo.InvariantCulture),
                other is null ? "missing" : other.Value.ToString(CultureInfo.InvariantCulture),
                difference?.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        if (differ > 0)
        {
            warnings.Add($"{differ} of {mine.Count} table{(mine.Count == 1 ? "" : "s")} differ{(exact ? "" : " (estimated counts: compare exactly before acting)")}.");
        }

        IReadOnlyList<SqlColumn> columns =
        [
            new("Table", "varchar", false),
            new($"{hereName} · {database}", "bigint", true),
            new($"{thereName} · {otherDatabase}", "bigint", true),
            new("Difference", "bigint", true),
        ];
        var section = new ToolSection(
            exact ? "Row counts (exact)" : "Row counts (estimated)",
            exact ? "count(*)" : "row_count()",
            [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
            null,
            null,
            "Tables marked for replication in the first database (every user table when none is). Counts taken a moment apart differ while changes are in flight.");
        return new ToolReport(warnings, [], [section], now);
    }

    private static async Task<Dictionary<string, long?>> TableCountsAsync(
        DbConnection connection, string database, bool exact, IReadOnlyCollection<string>? onlyNames, CancellationToken cancellationToken)
    {
        var db = Quote(database);
        var tables = await DbQuery.ListAsync(
            connection,
            $"""
            SELECT u.name, o.name, row_count(db_id(@db), o.id), CASE WHEN o.sysstat & -32768 = -32768 THEN 1 ELSE 0 END
            FROM {db}..sysobjects o JOIN {db}..sysusers u ON u.uid = o.uid
            WHERE o.type = 'U'
            ORDER BY u.name, o.name
            """,
            [("@db", database)],
            r => (Owner: DbQuery.Text(r, 0)!, Name: DbQuery.Text(r, 1)!, Estimate: DbQuery.Long(r, 2), Replicated: DbQuery.Int(r, 3) == 1),
            cancellationToken);

        var chosen = onlyNames is not null
            ? tables.Where(t => onlyNames.Contains($"{t.Owner}.{t.Name}", StringComparer.OrdinalIgnoreCase)).ToList()
            : tables.Any(t => t.Replicated) ? tables.Where(t => t.Replicated).ToList() : [.. tables];
        var counts = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in chosen.Take(MaxCompareTables))
        {
            var key = $"{table.Owner}.{table.Name}";
            counts[key] = exact
                ? (await DbQuery.ListAsync(connection, $"SELECT count_big(*) FROM {db}.{Quote(table.Owner)}.{Quote(table.Name)}", [], r => DbQuery.Long(r, 0), cancellationToken))[0]
                : table.Estimate;
        }

        return counts;
    }

    // ---- Helpers ----

    internal static string Quote(string name) => SqlObjectExplorer.Quote(name);

    private static double Number(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTime? Date(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A server time, as the reports show them (the database server's own clock).</summary>
    private static string Stamp(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "an unknown time";

    /// <summary>A grid's date without its milliseconds.</summary>
    private static string Trim(string? cell) => cell is { Length: > 19 } ? cell[..19] : cell ?? "an unknown time";

    private static double? Percent(double part, double whole) => whole > 0 ? Math.Clamp(100 * part / whole, 0, 100) : null;

    internal static string Mb(double mb) => mb.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) =>
        span < TimeSpan.Zero ? "0s"
        : span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalSeconds:0} s";
}
