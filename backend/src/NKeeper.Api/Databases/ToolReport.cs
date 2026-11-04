using System.Globalization;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Databases;

/// <summary>A script a report offers: SQL to open in a query window, or text to copy and run elsewhere (e.g. in RMAN).</summary>
/// <param name="Kind">"sql" (opens in a query window) or "rman" (copied: RMAN runs on the database server, not over SQL).</param>
public sealed record ToolScript(string Label, string Text, string Kind = "sql");

/// <summary>What one command of a report said: its result sets and messages, or why it failed; and scripts to act on it.</summary>
public sealed record ToolSection(string Title, string Command, IReadOnlyList<SqlOutput> Results, string? Error, IReadOnlyList<ToolScript>? Scripts = null, string? Note = null);

/// <summary>A DSI connection that is suspended or down: resuming it applies its queue to the replicate again.</summary>
/// <param name="Connection">"server.database", as <c>resume connection to</c> takes it.</param>
public sealed record SuspendedConnection(string Connection, string Thread, string State);

/// <summary>A Management report (Replication Health, Oracle Storage, Backups, Health).</summary>
/// <param name="Warnings">What needs looking at, worst first; empty when all is well.</param>
/// <param name="Suspended">Replication Server only: connections that can be resumed.</param>
public sealed record ToolReport(IReadOnlyList<string> Warnings, IReadOnlyList<SuspendedConnection> Suspended, IReadOnlyList<ToolSection> Sections, DateTimeOffset AtUtc);

internal static class ToolReports
{
    /// <summary>Every row of a section's result sets, with the columns it has.</summary>
    public static IEnumerable<(IReadOnlyList<SqlColumn> Columns, string?[] Row)> Rows(ToolSection section) =>
        section.Results.Where(r => r.Kind == "results" && r.Columns is not null && r.Rows is not null).SelectMany(r => r.Rows!.Select(row => (r.Columns!, row)));

    /// <summary>A row's value in the column of that name (any case), or null.</summary>
    public static string? Cell(IReadOnlyList<SqlColumn> columns, string?[] row, string name)
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

    public static double? Number(IReadOnlyList<SqlColumn> columns, string?[] row, string name) =>
        double.TryParse(Cell(columns, row, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
