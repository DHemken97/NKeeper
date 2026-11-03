using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NKeeper.Api.SqlServer;

/// <summary>One batch of a script: its text, the line it starts on (1-based), and how many times to run it (<c>GO 5</c>).</summary>
internal sealed record SqlBatch(string Text, int StartLine, int Count);

/// <summary>
/// Splits a script into batches at <c>GO</c> lines, as SSMS and sqlcmd do. <c>GO</c> is a client
/// command, not T-SQL: it only counts alone on its line (with an optional count and comment), and
/// not inside a string, a quoted identifier or a block comment.
/// </summary>
internal static partial class SqlBatches
{
    /// <summary>A batch can be repeated at most this many times.</summary>
    public const int MaxCount = 1000;

    public static IReadOnlyList<SqlBatch> Split(string script)
    {
        var batches = new List<SqlBatch>();
        var current = new StringBuilder();
        var startLine = 1;
        var state = new ScanState();
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (state.IsClean && GoLine().Match(line) is { Success: true } go)
            {
                var count = go.Groups[1].Success && int.TryParse(go.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
                Add(batches, current, startLine, Math.Clamp(count, 1, MaxCount));
                current.Clear();
                startLine = index + 2;
                continue;
            }

            state.Scan(line);
            if (current.Length > 0 || line.Length > 0 || index < lines.Length - 1)
            {
                current.Append(line).Append('\n');
            }
        }

        Add(batches, current, startLine, 1);
        return batches;
    }

    private static void Add(List<SqlBatch> batches, StringBuilder text, int startLine, int count)
    {
        var batch = text.ToString().TrimEnd();
        if (batch.Trim().Length > 0)
        {
            batches.Add(new SqlBatch(batch, startLine, count));
        }
    }

    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    /// <summary>Whether a line ends inside a string, quoted identifier or block comment.</summary>
    private sealed class ScanState
    {
        private int _commentDepth;
        private char _quote;

        public bool IsClean => _commentDepth == 0 && _quote == '\0';

        public void Scan(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (_commentDepth > 0)
                {
                    // T-SQL block comments nest.
                    if (c == '/' && next == '*')
                    {
                        _commentDepth++;
                        i++;
                    }
                    else if (c == '*' && next == '/')
                    {
                        _commentDepth--;
                        i++;
                    }
                }
                else if (_quote != '\0')
                {
                    if (c == _quote)
                    {
                        // A doubled closing character is an escaped one.
                        if (next == _quote)
                        {
                            i++;
                        }
                        else
                        {
                            _quote = '\0';
                        }
                    }
                }
                else if (c == '-' && next == '-')
                {
                    return;
                }
                else if (c == '/' && next == '*')
                {
                    _commentDepth = 1;
                    i++;
                }
                else if (c is '\'' or '"')
                {
                    _quote = c;
                }
                else if (c == '[')
                {
                    _quote = ']';
                }
            }
        }
    }
}
