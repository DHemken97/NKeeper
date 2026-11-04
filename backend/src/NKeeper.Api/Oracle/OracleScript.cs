using System.Text;
using System.Text.RegularExpressions;

namespace NKeeper.Api.Oracle;

/// <summary>A statement of a script, and the editor line it starts on.</summary>
/// <param name="PlSql">A PL/SQL block or unit: it keeps its semicolons.</param>
internal sealed record OracleStatement(string Text, int StartLine, bool PlSql);

/// <summary>
/// Splits a script into statements as SQL*Plus and SQL Developer do: a SQL statement ends at a
/// semicolon; a PL/SQL block (DECLARE, BEGIN, CREATE PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE…) ends at
/// a line holding only <c>/</c> (or the end of the script). Semicolons in strings and comments don't count.
/// <c>EXEC proc(…)</c> becomes a block; SQL*Plus settings (SET, SPOOL, PROMPT…) are skipped.
/// </summary>
internal static partial class OracleScript
{
    public static IReadOnlyList<OracleStatement> Split(string script)
    {
        var statements = new List<OracleStatement>();
        var text = new StringBuilder();
        var line = 1;
        var startLine = 1;
        var i = 0;
        var atLineStart = true;

        void Flush()
        {
            var statement = text.ToString().Trim();
            text.Clear();
            if (Meaningful(statement))
            {
                statements.Add(Shape(statement, startLine));
            }
        }

        while (i < script.Length)
        {
            if (atLineStart)
            {
                atLineStart = false;
                var end = script.IndexOf('\n', i);
                var whole = (end < 0 ? script[i..] : script[i..end]).Trim();
                if (whole == "/")
                {
                    Flush();
                    i = end < 0 ? script.Length : end + 1;
                    line++;
                    atLineStart = true;
                    continue;
                }

                if (!Meaningful(text.ToString()) && SqlPlusCommand().IsMatch(whole))
                {
                    text.Clear();
                    i = end < 0 ? script.Length : end + 1;
                    line++;
                    atLineStart = true;
                    continue;
                }
            }

            var c = script[i];
            if (text.Length == 0 && char.IsWhiteSpace(c))
            {
                if (c == '\n')
                {
                    line++;
                    atLineStart = true;
                }

                i++;
                continue;
            }

            if (text.Length == 0)
            {
                startLine = line;
            }

            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                var end = script.IndexOf('\n', i);
                end = end < 0 ? script.Length : end;
                text.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
                Append(script[i..end]);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                // q'[…]' quoting: up to the closing bracket and quote.
                if (c == '\'' && i > 0 && (script[i - 1] == 'q' || script[i - 1] == 'Q') && i + 1 < script.Length)
                {
                    var open = script[i + 1];
                    var close = open switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', _ => open };
                    var endQ = script.IndexOf($"{close}'", i + 2, StringComparison.Ordinal);
                    endQ = endQ < 0 ? script.Length : endQ + 2;
                    Append(script[i..endQ]);
                    continue;
                }

                var j = i + 1;
                while (j < script.Length)
                {
                    if (script[j] == c)
                    {
                        if (j + 1 < script.Length && script[j + 1] == c)
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                Append(script[i..Math.Min(j + 1, script.Length)]);
                continue;
            }

            if (c == ';' && !IsPlSql(text.ToString()))
            {
                i++;
                Flush();
                continue;
            }

            text.Append(c);
            if (c == '\n')
            {
                line++;
                atLineStart = true;
            }

            i++;
        }

        Flush();
        return statements;

        void Append(string part)
        {
            text.Append(part);
            line += part.Count(ch => ch == '\n');
            i += part.Length;
        }
    }

    /// <summary>Whether a statement starts a PL/SQL block or unit (after its comments).</summary>
    public static bool IsPlSql(string statement) => PlSqlStart().IsMatch(WithoutComments(statement).TrimStart());

    private static bool Meaningful(string statement) => WithoutComments(statement).Trim().Length > 0;

    private static OracleStatement Shape(string statement, int startLine)
    {
        var exec = Exec().Match(WithoutComments(statement).Trim());
        if (exec.Success)
        {
            return new OracleStatement($"BEGIN {exec.Groups[2].Value.TrimEnd().TrimEnd(';')}; END;", startLine, true);
        }

        return new OracleStatement(statement, startLine, IsPlSql(statement));
    }

    private static string WithoutComments(string statement) => Comments().Replace(statement, " ");

    [GeneratedRegex(@"^(DECLARE|BEGIN|CREATE\s+(OR\s+REPLACE\s+)?((NON)?EDITIONABLE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE|LIBRARY|JAVA))\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlSqlStart();

    [GeneratedRegex(@"^EXEC(UTE)?\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExecPattern();

    private static Regex Exec() => ExecPattern();

    [GeneratedRegex(@"^(SET\s+(SERVEROUTPUT|ECHO|FEEDBACK|LINESIZE|LINES|PAGESIZE|PAGES|TIMING|DEFINE|VERIFY|HEADING|TERMOUT|TRIMSPOOL|LONG|SQLBLANKLINES|AUTOCOMMIT)|SPOOL|PROMPT|SHOW|COLUMN|COL|TTITLE|BTITLE|BREAK|COMPUTE|WHENEVER|DEFINE|UNDEFINE|CLEAR|REM|REMARK|PAUSE|ACCEPT)(\s.*)?;?$", RegexOptions.IgnoreCase)]
    private static partial Regex SqlPlusCommand();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
}
