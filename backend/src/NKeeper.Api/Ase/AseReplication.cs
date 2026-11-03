using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>What one command of the report said: its result sets and messages, or why it failed.</summary>
public sealed record ReplicationSection(string Title, string Command, IReadOnlyList<SqlOutput> Results, string? Error);

/// <summary>A DSI connection that is suspended or down: resuming it applies its queue to the replicate again.</summary>
/// <param name="Connection">"server.database", as <c>resume connection to</c> takes it.</param>
public sealed record SuspendedConnection(string Connection, string Thread, string State);

/// <param name="Warnings">What needs looking at, worst first; empty when all is well.</param>
public sealed record ReplicationReport(IReadOnlyList<string> Warnings, IReadOnlyList<SuspendedConnection> Suspended, IReadOnlyList<ReplicationSection> Sections, DateTimeOffset AtUtc);

/// <summary>
/// Replication health. On ASE (the primary side): each database's transaction log, whether replication
/// holds its truncation point, and the Rep Agents. On Replication Server: what's down, its threads, queues,
/// stable devices and warm standby, and resuming a suspended connection.
/// </summary>
internal static partial class AseReplication
{
    public const string Tool = "replication";

    /// <summary>A log at least this full is reported.</summary>
    private const double FullLogPercent = 80;

    /// <summary>Stable devices at least this full are reported: when they fill, replication stops.</summary>
    private const double FullQueuePercent = 70;

    private const int RowsPerResult = 500;

    public static SqlObjectNode ToolNode => new() { Kind = Kinds.Tool, Label = "Replication Health", Detail = "Rep Agents, logs, queues", Name = Tool };

    // ---- ASE (primary) ----

    public static async Task<ReplicationReport> PrimaryAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ReplicationSection>();

        const string logSql = """
            SELECT d.name,
                   (SELECT sum(u.size) FROM master..sysusages u WHERE u.dbid = d.dbid AND u.segmap & 4 = 4),
                   lct_admin('logsegment_freepages', d.dbid),
                   @@maxpagesize,
                   (SELECT min(h.starttime) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.name = '$replication_truncation_point'),
                   (SELECT min(h.spid) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.spid <> 0),
                   (SELECT min(h.starttime) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.spid <> 0)
            FROM master..sysdatabases d
            WHERE d.status2 & 16 = 0
            ORDER BY d.name
            """;
        try
        {
            var logs = await DbQuery.ListAsync(
                connection,
                logSql,
                [],
                r => new
                {
                    Name = DbQuery.Text(r, 0)!,
                    Pages = DbQuery.Long(r, 1) ?? 0,
                    FreePages = DbQuery.Long(r, 2) ?? 0,
                    PageBytes = DbQuery.Long(r, 3) ?? 2048,
                    ReplicationSince = r.IsDBNull(4) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture),
                    OldestSpid = r.IsDBNull(5) ? (int?)null : DbQuery.Int(r, 5),
                    OldestSince = r.IsDBNull(6) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(6), CultureInfo.InvariantCulture),
                },
                cancellationToken);

            var rows = new List<string?[]>();
            foreach (var log in logs)
            {
                var mb = (double)log.Pages * log.PageBytes / (1024 * 1024);
                var freeMb = (double)log.FreePages * log.PageBytes / (1024 * 1024);
                var usedPercent = log.Pages > 0 ? Math.Clamp(100 * (1 - ((double)log.FreePages / log.Pages)), 0, 100) : 0;
                rows.Add(
                [
                    log.Name,
                    mb.ToString("0.0", CultureInfo.InvariantCulture),
                    freeMb.ToString("0.0", CultureInfo.InvariantCulture),
                    usedPercent.ToString("0", CultureInfo.InvariantCulture),
                    log.ReplicationSince?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    log.OldestSpid is { } spid ? $"spid {spid} since {log.OldestSince:yyyy-MM-dd HH:mm:ss}" : null,
                ]);

                if (log.Pages > 0 && usedPercent >= FullLogPercent)
                {
                    warnings.Add(log.ReplicationSince is not null
                        ? $"{log.Name}: the log is {usedPercent:0}% full and replication holds its truncation point (since {log.ReplicationSince:yyyy-MM-dd HH:mm}). If the Rep Agent is behind or down, the log can't be truncated."
                        : $"{log.Name}: the log is {usedPercent:0}% full.");
                }
            }

            IReadOnlyList<SqlColumn> columns =
            [
                new("Database", "varchar", false),
                new("Log MB", "numeric", true),
                new("Free MB", "numeric", true),
                new("Used %", "numeric", true),
                new("Replication point since", "datetime", false),
                new("Oldest open transaction", "varchar", false),
            ];
            sections.Add(new ReplicationSection(
                "Transaction logs",
                "master..sysusages, lct_admin('logsegment_freepages'), master..syslogshold",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null));
        }
        catch (DbException ex)
        {
            sections.Add(new ReplicationSection("Transaction logs", "master..sysusages, master..syslogshold", [], ex.Message));
        }

        sections.Add(await SectionAsync(connection, "Rep Agents", "sp_help_rep_agent", cancellationToken));
        return new ReplicationReport(warnings, [], sections, now);
    }

    // ---- Replication Server ----

    public static async Task<ReplicationReport> ReplicationServerAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var suspended = new Dictionary<string, SuspendedConnection>(StringComparer.OrdinalIgnoreCase);

        var health = await SectionAsync(connection, "Health", "admin health", cancellationToken);
        var down = await SectionAsync(connection, "Down", "admin who_is_down", cancellationToken);
        var who = await SectionAsync(connection, "Threads", "admin who", cancellationToken);
        var queues = await SectionAsync(connection, "Queues", "admin who, sqm", cancellationToken);
        var disk = await SectionAsync(connection, "Stable devices", "admin disk_space", cancellationToken);
        var standby = await SectionAsync(connection, "Warm standby", "admin logical_status", cancellationToken);

        var downCount = Rows(down).Count();
        if (downCount > 0)
        {
            warnings.Add($"{downCount} thread{(downCount == 1 ? " is" : "s are")} down or suspended (admin who_is_down).");
        }

        foreach (var section in new[] { down, who })
        {
            foreach (var (columns, row) in Rows(section))
            {
                var name = Cell(columns, row, "Name");
                var state = Cell(columns, row, "State");
                var info = Cell(columns, row, "Info");
                if (name is null || state is null || info is null || !name.StartsWith("DSI", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((state.Contains("Suspend", StringComparison.OrdinalIgnoreCase) || state.Contains("Down", StringComparison.OrdinalIgnoreCase))
                    && ConnectionInInfo().Match(info) is { Success: true } match)
                {
                    suspended.TryAdd(match.Value, new SuspendedConnection(match.Value, name, state));
                }
            }
        }

        foreach (var (columns, row) in Rows(disk))
        {
            if (long.TryParse(Cell(columns, row, "Total Segs"), CultureInfo.InvariantCulture, out var total)
                && long.TryParse(Cell(columns, row, "Used Segs"), CultureInfo.InvariantCulture, out var used)
                && total > 0
                && 100.0 * used / total >= FullQueuePercent)
            {
                warnings.Add($"Stable device {Cell(columns, row, "Partition") ?? Cell(columns, row, "Logical") ?? "?"} is {100.0 * used / total:0}% full: when it fills, replication stops.");
            }
        }

        if (health.Error is null && Rows(health).Any(x => x.Row.Any(cell => cell?.Contains("SUSPECT", StringComparison.OrdinalIgnoreCase) == true)))
        {
            warnings.Insert(0, "Replication Server reports itself SUSPECT (admin health).");
        }

        return new ReplicationReport(warnings, [.. suspended.Values], [health, down, who, queues, disk, standby], now);
    }

    /// <summary>Resumes a suspended DSI connection, optionally skipping the transaction that failed (it goes to the exceptions log).</summary>
    public static async Task ResumeAsync(DbConnection connection, string target, bool skipTransaction, CancellationToken cancellationToken)
    {
        if (!ConnectionName().IsMatch(target))
        {
            throw new SqlExplorerException("Name the connection as server.database.");
        }

        await DbQuery.ExecuteAsync(connection, $"resume connection to {target}{(skipTransaction ? " skip transaction" : string.Empty)}", cancellationToken);
    }

    // ---- Helpers ----

    /// <summary>Runs a command and keeps everything it returned; a failure is the section's error, not the report's.</summary>
    private static async Task<ReplicationSection> SectionAsync(DbConnection connection, string title, string command, CancellationToken cancellationToken)
    {
        var results = new List<SqlOutput>();
        var ase = connection as AseConnection;

        void OnInfo(object sender, AseInfoMessageEventArgs e)
        {
            lock (results)
            {
                foreach (AseError error in e.Errors)
                {
                    if (error.Severity <= 10 && !string.IsNullOrWhiteSpace(error.Message))
                    {
                        results.Add(new SqlOutput("message", error.Message.TrimEnd()));
                    }
                }
            }
        }

        if (ase is not null)
        {
            ase.InfoMessage += OnInfo;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = command;
            cmd.CommandTimeout = 60;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            do
            {
                if (reader.FieldCount > 0)
                {
                    var result = await AseDialect.ReadResultAsync(reader, RowsPerResult, cancellationToken);
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken));

            return new ReplicationSection(title, command, results, null);
        }
        catch (DbException ex)
        {
            return new ReplicationSection(title, command, results, ex.Message);
        }
        finally
        {
            if (ase is not null)
            {
                ase.InfoMessage -= OnInfo;
            }
        }
    }

    private static IEnumerable<(IReadOnlyList<SqlColumn> Columns, string?[] Row)> Rows(ReplicationSection section) =>
        section.Results.Where(r => r.Kind == "results" && r.Columns is not null && r.Rows is not null).SelectMany(r => r.Rows!.Select(row => (r.Columns!, row)));

    private static string? Cell(IReadOnlyList<SqlColumn> columns, string?[] row, string name)
    {
        for (var i = 0; i < columns.Count && i < row.Length; i++)
        {
            if (string.Equals(columns[i].Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return row[i]?.Trim();
            }
        }

        return null;
    }

    /// <summary>A DSI thread's Info, e.g. "101 PDS.pdb" or "102(1) LDS.ldb": the connection is the server.database part.</summary>
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_$#@]*\.[A-Za-z_][A-Za-z0-9_$#@]*")]
    private static partial Regex ConnectionInInfo();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#@]*\.[A-Za-z_][A-Za-z0-9_$#@]*$")]
    private static partial Regex ConnectionName();
}
