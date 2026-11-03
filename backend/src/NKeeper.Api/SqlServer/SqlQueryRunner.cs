using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace NKeeper.Api.SqlServer;

/// <summary>A result set's column: its name and SQL Server type, and whether it holds numbers (shown right-aligned).</summary>
public sealed record SqlColumn(string Name, string Type, bool Numeric);

/// <summary>
/// One thing a query produced, in the order it happened, as SSMS lists them:
/// <list type="bullet">
/// <item><c>results</c>: a result set (<see cref="Columns"/>, <see cref="Rows"/>; <see cref="RowCount"/> is every row, even past the ones sent).</item>
/// <item><c>rows</c>: "(n rows affected)" after a statement.</item>
/// <item><c>message</c>: PRINT, or an informational message (<see cref="Text"/>).</item>
/// <item><c>error</c>: an error, with its number, severity, state, procedure and line in the script.</item>
/// </list>
/// </summary>
public sealed record SqlOutput(
    string Kind,
    string? Text = null,
    IReadOnlyList<SqlColumn>? Columns = null,
    IReadOnlyList<string?[]>? Rows = null,
    long? RowCount = null,
    bool Truncated = false,
    int? Number = null,
    int? Severity = null,
    int? State = null,
    string? Procedure = null,
    int? Line = null);

/// <param name="Database">The session's database afterwards (<c>USE</c> changes it).</param>
/// <param name="Cancelled">The user cancelled it.</param>
/// <param name="ConnectionLost">The connection broke (a severe error, or the server went away): the session must reconnect.</param>
public sealed record SqlQueryResult(IReadOnlyList<SqlOutput> Output, string Database, long ElapsedMs, bool Cancelled, bool ConnectionLost, bool HasErrors);

/// <summary>Runs scripts on a session's connection, collecting results and messages as SSMS shows them.</summary>
internal static class SqlQueryRunner
{
    /// <summary>Rows sent per result set; the rest are counted, not sent.</summary>
    public const int MaxRowsPerResult = 10_000;

    /// <summary>Rows sent in all, across a script's result sets.</summary>
    public const int MaxRowsTotal = 50_000;

    /// <summary>Characters sent per value (SSMS's grid default is 65,535).</summary>
    public const int MaxCharsPerValue = 65_535;

    /// <param name="lineOffset">Lines before <paramref name="script"/> in the editor (when running a selection), so errors point at the editor's lines.</param>
    public static async Task<SqlQueryResult> RunAsync(SqlConnection connection, string script, int lineOffset, CancellationToken cancellationToken)
    {
        var output = new List<SqlOutput>();
        var sent = 0;
        var cancelled = false;
        var hasErrors = false;
        var watch = Stopwatch.StartNew();
        var batchStart = 1;

        void OnInfo(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                lock (output)
                {
                    hasErrors |= error.Class > 10;
                    output.Add(Describe(error, batchStart + lineOffset));
                }
            }
        }

        // Errors of severity 16 and below arrive as messages, so a script carries on past them like in SSMS.
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += OnInfo;
        try
        {
            foreach (var batch in SqlBatches.Split(script))
            {
                batchStart = batch.StartLine;
                for (var run = 0; run < batch.Count; run++)
                {
                    await using var command = new SqlCommand(batch.Text, connection) { CommandTimeout = 0 };
                    command.StatementCompleted += (_, e) =>
                    {
                        lock (output)
                        {
                            output.Add(new SqlOutput("rows", $"({e.RecordCount} {(e.RecordCount == 1 ? "row" : "rows")} affected)", RowCount: e.RecordCount));
                        }
                    };

                    try
                    {
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        do
                        {
                            if (reader.FieldCount > 0)
                            {
                                // Its place is where it starts: messages raised while reading it (rows affected) come after it.
                                int slot;
                                lock (output)
                                {
                                    slot = output.Count;
                                    output.Add(new SqlOutput("results"));
                                }

                                var result = await ReadResultAsync(reader, MaxRowsTotal - sent, cancellationToken);
                                sent += result.Rows!.Count;
                                lock (output)
                                {
                                    output[slot] = result;
                                }
                            }
                        }
                        while (await reader.NextResultAsync(cancellationToken));
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || (ex is SqlException && cancellationToken.IsCancellationRequested))
                    {
                        cancelled = true;
                        output.Add(new SqlOutput("error", "Query was cancelled by user."));
                        break;
                    }
                    catch (SqlException ex)
                    {
                        // Severity 17 and up, or a broken connection: report it and stop if the connection went.
                        hasErrors = true;
                        lock (output)
                        {
                            output.AddRange(ex.Errors.Cast<SqlError>().Select(e => Describe(e, batch.StartLine + lineOffset)));
                        }

                        if (connection.State != ConnectionState.Open)
                        {
                            return new SqlQueryResult(output, string.Empty, watch.ElapsedMilliseconds, false, true, true);
                        }
                    }
                }

                if (cancelled)
                {
                    break;
                }
            }
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }

        return new SqlQueryResult(output, connection.State == ConnectionState.Open ? connection.Database : string.Empty, watch.ElapsedMilliseconds, cancelled, connection.State != ConnectionState.Open, hasErrors);
    }

    private static SqlOutput Describe(SqlError error, int batchStartLine) =>
        error.Class > 10
            ? new SqlOutput(
                "error",
                error.Message,
                Number: error.Number,
                Severity: error.Class,
                State: error.State,
                Procedure: string.IsNullOrEmpty(error.Procedure) ? null : error.Procedure,
                // Inside a procedure the line is the procedure's own; otherwise the batch's, made the script's.
                Line: error.LineNumber <= 0 ? null : string.IsNullOrEmpty(error.Procedure) ? batchStartLine + error.LineNumber - 1 : error.LineNumber)
            : new SqlOutput("message", error.Message);

    private static async Task<SqlOutput> ReadResultAsync(SqlDataReader reader, int budget, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => Column(reader, i)).ToList();
        var limit = Math.Max(0, Math.Min(MaxRowsPerResult, budget));
        var rows = new List<string?[]>();
        long count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (count++ < limit)
            {
                var row = new string?[columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = SqlValueText.Read(reader, i, MaxCharsPerValue);
                }

                rows.Add(row);
            }
        }

        return new SqlOutput("results", Columns: columns, Rows: rows, RowCount: count, Truncated: count > rows.Count);
    }

    private static SqlColumn Column(SqlDataReader reader, int ordinal)
    {
        var type = reader.GetDataTypeName(ordinal);
        var name = reader.GetName(ordinal);
        return new SqlColumn(
            string.IsNullOrEmpty(name) ? "(No column name)" : name,
            type,
            type is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real");
    }
}

/// <summary>Values as SSMS shows them: bits as 1/0, binary as 0x…, dates as yyyy-MM-dd HH:mm:ss.fff.</summary>
internal static class SqlValueText
{
    public static string? Read(SqlDataReader reader, int ordinal, int maxChars)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var type = reader.GetDataTypeName(ordinal);
        string text;
        try
        {
            text = type switch
            {
                "decimal" or "numeric" => reader.GetSqlDecimal(ordinal).ToString(),
                "money" or "smallmoney" => reader.GetDecimal(ordinal).ToString("0.0000", CultureInfo.InvariantCulture),
                "datetime" => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                "smalldatetime" => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "datetime2" => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                "date" => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "datetimeoffset" => reader.GetDateTimeOffset(ordinal).ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
                _ => Format(reader.GetValue(ordinal), maxChars),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FileNotFoundException or FileLoadException or TypeLoadException or InvalidOperationException)
        {
            // CLR types (geography, hierarchyid…) need their assembly to be read as values: show their bytes.
            text = reader.GetSqlBytes(ordinal) is { IsNull: false } bytes ? Hex(bytes.Value, maxChars) : $"({type})";
        }

        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static string Format(object value, int maxChars) => value switch
    {
        bool b => b ? "1" : "0",
        byte[] bytes => Hex(bytes, maxChars),
        Guid guid => guid.ToString("D").ToUpperInvariant(),
        TimeSpan time => time.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Hex(byte[] bytes, int maxChars)
    {
        var take = Math.Min(bytes.Length, (maxChars - 2) / 2);
        return "0x" + Convert.ToHexString(bytes, 0, take);
    }
}
