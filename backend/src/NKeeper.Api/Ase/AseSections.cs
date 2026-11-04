using System.Data.Common;
using AdoNetCore.AseClient;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Ase;

/// <summary>Runs one command of an ASE or Replication Server report into a section: its result sets and messages, or why it failed.</summary>
internal static class AseSections
{
    private const int RowsPerResult = 500;

    /// <summary>Runs a command and keeps everything it returned; a failure is the section's error, not the report's.</summary>
    public static async Task<ToolSection> RunAsync(DbConnection connection, string title, string command, CancellationToken cancellationToken)
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

            return new ToolSection(title, command, results, null);
        }
        catch (DbException ex)
        {
            return new ToolSection(title, command, results, ex.Message);
        }
        finally
        {
            if (ase is not null)
            {
                ase.InfoMessage -= OnInfo;
            }
        }
    }

    /// <summary>
    /// A section shown as <paramref name="source"/> (the tables it reads), whose query falls back to
    /// <paramref name="fallbackSql"/> when it fails (e.g. without monitoring).
    /// </summary>
    public static async Task<ToolSection> RunAsync(
        DbConnection connection, string title, string source, string sql, CancellationToken cancellationToken, string? fallbackSql = null, string? note = null)
    {
        var section = await RunAsync(connection, title, sql, cancellationToken);
        if (section.Error is not null && fallbackSql is not null)
        {
            var fallback = await RunAsync(connection, title, fallbackSql, cancellationToken);
            if (fallback.Error is null)
            {
                return fallback with { Command = source, Note = $"{note} Monitoring couldn't be read ({section.Error.Trim()}): showing what the server keeps itself.".Trim() };
            }
        }

        return section with { Command = source, Note = note };
    }
}
