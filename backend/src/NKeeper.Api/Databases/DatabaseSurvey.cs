using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Ase;
using NKeeper.Api.Oracle;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Databases;

/// <summary>
/// Something a survey found that answers one of the insurance questionnaire's questions, e.g. "Full backups
/// of 4 databases, about every day". <see cref="Database"/> names the database (Oracle: schema) it's about;
/// null when it's about the whole server.
/// </summary>
public sealed record SurveyFact(string Topic, string Text, string? Database = null);

/// <summary>A database (Oracle: an application schema) on the server.</summary>
/// <param name="System">The server's own (master, SYS…), not the business's.</param>
public sealed record SurveyDatabase(string Name, double? SizeMb, string? Created, string? Owner, string? State, string? Location, bool System);

/// <summary>
/// What a database server says about itself for the insurance questionnaire: its software, databases, who can
/// change data, auditing, routine deletion and backups. The facts answer the questions; the sections are what
/// they were read from, for the report's appendix. Read-only: nothing is changed on the server.
/// </summary>
public sealed record DatabaseSurvey(
    string Product,
    string? Version,
    string Developer,
    IReadOnlyList<SurveyDatabase> Databases,
    IReadOnlyList<SurveyFact> Facts,
    IReadOnlyList<ToolSection> Sections,
    DateTimeOffset AtUtc);

/// <summary>The questions a fact answers, as the questionnaire's page groups them.</summary>
internal static class SurveyTopics
{
    public const string Software = "software";
    public const string Contents = "contents";
    public const string Writers = "writers";
    public const string Location = "location";
    public const string Deletion = "deletion";
    public const string ChangeLog = "changeLog";
    public const string BackupTypes = "backupTypes";
    public const string BackupData = "backupData";
    public const string BackupSchedule = "backupSchedule";
    public const string BackupRetention = "backupRetention";
    public const string BackupLabels = "backupLabels";
    public const string BackupRestore = "backupRestore";
    public const string BackupIndex = "backupIndex";
    public const string BackupMedia = "backupMedia";
    public const string BackupRemote = "backupRemote";
    public const string BackupLocation = "backupLocation";
}

internal static class DatabaseSurveys
{
    public static Task<DatabaseSurvey> RunAsync(DatabaseEngine engine, DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken) => engine switch
    {
        DatabaseEngine.SqlServer => SqlSurvey.RunAsync(connection, now, cancellationToken),
        DatabaseEngine.Oracle => OracleSurvey.RunAsync(connection, now, cancellationToken),
        DatabaseEngine.Ase => AseSurvey.RunAsync(connection, now, cancellationToken),
        _ => throw new SqlExplorerException("Replication Server holds no business data of its own: survey the ASE servers it replicates between."),
    };
}

/// <summary>Runs a survey's queries, each into a section (a failure is that section's error), and collects its facts.</summary>
internal sealed class SurveyBuilder(IDatabaseDialect dialect, DbConnection connection, CancellationToken cancellationToken)
{
    /// <summary>One slow query (a huge backup history) mustn't hold up the rest.</summary>
    private static readonly TimeSpan SectionTimeout = TimeSpan.FromSeconds(90);

    public List<SurveyFact> Facts { get; } = [];

    public List<ToolSection> Sections { get; } = [];

    public DbConnection Connection => connection;

    public CancellationToken CancellationToken => cancellationToken;

    public void Fact(string topic, string text, string? database = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Facts.Add(new SurveyFact(topic, text.Trim(), database));
        }
    }

    /// <summary>Runs a query into a section of the appendix; when it fails and there's a fallback, that runs instead.</summary>
    /// <param name="optional">Something older versions or editions lack: left out of the appendix when it fails.</param>
    public async Task<ToolSection> SectionAsync(string title, string source, string sql, string? fallbackSql = null, string? note = null, bool appendix = true, bool optional = false)
    {
        var section = await RunAsync(title, source, sql, note);
        if (section.Error is not null && fallbackSql is not null)
        {
            var fallback = await RunAsync(title, source, fallbackSql, note);
            if (fallback.Error is null)
            {
                section = fallback;
            }
        }

        if (appendix && !(optional && section.Error is not null))
        {
            Sections.Add(section);
        }

        return section;
    }

    /// <summary>
    /// Runs one query per item (e.g. per database) and puts their rows together in one section; the items it
    /// couldn't read are listed in the section's note rather than failing it.
    /// </summary>
    public async Task<ToolSection> MergedSectionAsync(string title, string source, IEnumerable<(string Item, string Sql, string? Fallback)> queries, string? note = null)
    {
        IReadOnlyList<SqlColumn>? columns = null;
        var rows = new List<string?[]>();
        var failed = new List<string>();
        foreach (var (item, sql, fallback) in queries)
        {
            var section = await SectionAsync(title, source, sql, fallback, appendix: false);
            if (section.Error is not null)
            {
                failed.Add($"{item} ({Shorten(section.Error, 120)})");
                continue;
            }

            foreach (var result in section.Results.Where(r => r.Kind == "results" && r.Columns is not null && r.Rows is not null))
            {
                columns ??= result.Columns;
                if (result.Columns!.Count == columns!.Count)
                {
                    rows.AddRange(result.Rows!);
                }
            }
        }

        var notes = new List<string>();
        if (note is not null)
        {
            notes.Add(note);
        }

        if (failed.Count > 0)
        {
            notes.Add($"Couldn't read: {string.Join("; ", failed)}.");
        }

        var merged = new ToolSection(
            title,
            source,
            columns is null ? [] : [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
            columns is null && failed.Count > 0 ? string.Join("; ", failed) : null,
            null,
            notes.Count > 0 ? string.Join(" ", notes) : null);
        Sections.Add(merged);
        return merged;
    }

    /// <summary>A section NKeeper worked out from others (e.g. jobs with their schedules in words).</summary>
    public ToolSection Derived(string title, string source, IReadOnlyList<string> columns, IEnumerable<string?[]> rows, string? note = null)
    {
        var list = rows.ToList();
        var section = new ToolSection(
            title,
            source,
            [new SqlOutput("results", Columns: [.. columns.Select(c => new SqlColumn(c, "text", false))], Rows: list, RowCount: list.Count)],
            null,
            null,
            note);
        Sections.Add(section);
        return section;
    }

    private async Task<ToolSection> RunAsync(string title, string source, string sql, string? note)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SectionTimeout);
        SqlQueryResult result;
        try
        {
            result = await dialect.RunAsync(connection, sql, 0, timeout.Token);
        }
        catch (Exception ex) when (DbErrors.Is(ex) || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ToolSection(title, source, [], ex.Message, null, note);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var errors = result.Output.Where(o => o.Kind == "error").Select(o => o.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var error = result.Cancelled
            ? $"It took longer than {SectionTimeout.TotalSeconds:0} seconds, so it was stopped."
            : result.HasErrors ? string.Join(" ", errors.DefaultIfEmpty("The server reported an error.")) : null;
        var kept = result.Output.Where(o => o.Kind is "results" or "message").ToList();
        return new ToolSection(title, source, kept, error, null, note);
    }

    // ---- Reading rows ----

    public static IEnumerable<(IReadOnlyList<SqlColumn> Columns, string?[] Row)> Rows(ToolSection section) => ToolReports.Rows(section);

    public static string? Cell(IReadOnlyList<SqlColumn> columns, string?[] row, string name) => ToolReports.Cell(columns, row, name) is { Length: > 0 } text ? text : null;

    public static double? Number(IReadOnlyList<SqlColumn> columns, string?[] row, string name) => ToolReports.Number(columns, row, name);

    // ---- Wording ----

    public static string Shorten(string? text, int length = 200) =>
        text is null ? string.Empty : text.ReplaceLineEndings(" ").Trim() is var one && one.Length > length ? $"{one[..(length - 1)]}…" : one;

    public static string Plural(long count, string noun, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? noun : plural ?? noun + "s")}";

    /// <summary>"a, b and c", with "and n more" past <paramref name="max"/>.</summary>
    public static string List(IEnumerable<string> items, int max = 12)
    {
        var all = items.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0)
        {
            return string.Empty;
        }

        var shown = all.Take(max).ToList();
        if (all.Count > max)
        {
            shown.Add($"{all.Count - max} more");
        }

        return shown.Count == 1 ? shown[0] : $"{string.Join(", ", shown[..^1])} and {shown[^1]}";
    }

    /// <summary>How often something happened, from how many times it did in a number of days: "about every day", "about every 7 days".</summary>
    public static string Frequency(double count, double days)
    {
        if (count <= 0)
        {
            return $"none in the last {days:0} days";
        }

        var interval = days / count;
        return interval switch
        {
            < 0.75 => $"about {count / days:0.#} a day",
            < 1.5 => "about every day",
            < 6 => $"about every {interval:0} days",
            < 9 => "about every week",
            < 25 => $"about every {interval:0} days",
            _ => "about every month or less often",
        };
    }

    /// <summary>The folder of a file's path (either kind of slash), or the path when it has none.</summary>
    public static string Folder(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut > 0 ? path[..cut] : path;
    }

    /// <summary>Whether a backup path is somewhere other than the server's own disks.</summary>
    public static string? Remote(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("https://", StringComparison.Ordinal) || lower.StartsWith("s3://", StringComparison.Ordinal))
        {
            return lower.Contains("blob.core.windows.net", StringComparison.Ordinal) ? "Azure Blob Storage" : lower.StartsWith("s3://", StringComparison.Ordinal) || lower.Contains("amazonaws.com", StringComparison.Ordinal) ? "Amazon S3 (or S3-compatible storage)" : "cloud storage (URL)";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var server = path.TrimStart('\\').Split('\\')[0];
            return $"a network share on {server}";
        }

        return null;
    }
}
