using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;
using Renci.SshNet;

namespace NKeeper.Api.Journal;

/// <summary>One journal entry.</summary>
/// <param name="Cursor">Where it is in the journal, to read on from.</param>
/// <param name="Priority">syslog's: 0 (emergency) to 7 (debug); null when it has none.</param>
/// <param name="Unit">The systemd unit it came from.</param>
/// <param name="Identifier">The program's name for itself, e.g. <c>sshd</c>.</param>
public sealed record JournalEntry(string Cursor, DateTimeOffset? Time, int? Priority, string? Unit, string? Identifier, string? Pid, string? Hostname, string Message);

/// <summary>A page of entries, newest first.</summary>
/// <param name="HasMore">Older matching entries remain: ask with the last entry's cursor.</param>
/// <param name="Limited">The login could only read part of the journal: it isn't root and can't use sudo without a password.</param>
public sealed record JournalPage(IReadOnlyList<JournalEntry> Entries, bool HasMore, bool Limited);

/// <summary>What to read from the journal.</summary>
/// <param name="MaxPriority">This priority and worse.</param>
/// <param name="Search">A regular expression on the message (journalctl's <c>--grep</c>).</param>
/// <param name="After">Continue past this cursor, in the direction of reading.</param>
internal sealed record JournalQuery(DateTimeOffset? Since, DateTimeOffset? Until, int? MaxPriority, string? Unit, string? Identifier, string? Search, string? After);

/// <summary>A Linux host's systemd journal, read with <c>journalctl</c> over SSH.</summary>
internal static partial class JournalHost
{
    public const int PageSize = 200;

    private const string Preamble = SshScript.RootIfAllowed + "command -v journalctl >/dev/null 2>&1 || { echo 'This host has no journalctl (systemd).' >&2; exit 127; }; ";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(50);

    public static async Task<JournalPage> ReadAsync(SshClient client, JournalQuery query, CancellationToken cancellationToken)
    {
        var (exit, output, errors) = await SshScript.RunAsync(client, ReadScript(query), Timeout, "Reading the journal", cancellationToken);
        var (text, limited) = SshScript.WithoutLimitedMarker(output);
        var message = errors.Trim();

        // With --grep, journalctl exits 1 when nothing matches, saying nothing.
        if (exit != 0 && message.Length > 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, Explain(message));
        }

        var entries = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(ParseEntry).OfType<JournalEntry>().ToList();
        var hasMore = entries.Count > PageSize;
        return new JournalPage(hasMore ? entries[..PageSize] : entries, hasMore, limited || message.Contains("not seeing messages", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Newest first, one more than a page to tell whether older entries remain.</summary>
    internal static string ReadScript(JournalQuery query) =>
        Preamble + "$p journalctl --no-pager --output=json --reverse " + string.Create(CultureInfo.InvariantCulture, $"--lines={PageSize + 1}") + Filters(query, includeTimes: true);

    /// <summary>From <paramref name="query"/>'s cursor (or from now) until stopped; a follow has no time limits.</summary>
    internal static string FollowScript(JournalQuery query) =>
        Preamble + "exec $p journalctl --no-pager --output=json --follow" + (query.After is null ? " --lines=0" : string.Empty) + Filters(query, includeTimes: false);

    private static string Filters(JournalQuery query, bool includeTimes)
    {
        var script = new StringBuilder();
        if (includeTimes && query.Since is { } since)
        {
            script.Append(CultureInfo.InvariantCulture, $" --since=@{since.ToUnixTimeSeconds()}");
        }

        if (includeTimes && query.Until is { } until)
        {
            script.Append(CultureInfo.InvariantCulture, $" --until=@{until.ToUnixTimeSeconds()}");
        }

        if (query.MaxPriority is { } priority)
        {
            script.Append(CultureInfo.InvariantCulture, $" --priority={priority}");
        }

        // Each value is one word after its option's "=", so it can't be taken for an option.
        Option(script, "unit", query.Unit);
        Option(script, "identifier", query.Identifier);
        Option(script, "grep", query.Search);
        Option(script, "after-cursor", query.After);
        return script.ToString();
    }

    private static void Option(StringBuilder script, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            script.Append(" --").Append(name).Append('=').Append(SshScript.Quote(value.Trim()));
        }
    }

    public static bool IsCursor(string? value) => value is null || CursorFormat().IsMatch(value);

    /// <summary>What journalctl said, with what to do about the usual causes.</summary>
    internal static string Explain(string message)
    {
        if (message.Contains("grep", StringComparison.OrdinalIgnoreCase) && message.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase))
        {
            return "This host's journalctl can't search messages (that needs systemd 237 or later). Leave Search empty, and use Find on the entries shown.";
        }

        if (message.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase))
        {
            return "This login can't read the journal. Use root, a user who may use sudo without a password, or one in the systemd-journal group.";
        }

        return message.StartsWith("Bad pattern", StringComparison.Ordinal) ? $"The search isn't a valid regular expression: {message}" : message;
    }

    /// <summary>One line of <c>journalctl --output=json</c>; null when it isn't an entry.</summary>
    internal static JournalEntry? ParseEntry(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Field(root, "__CURSOR") is not { } cursor)
            {
                return null;
            }

            return new JournalEntry(
                cursor,
                long.TryParse(Field(root, "__REALTIME_TIMESTAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out var micros) && micros / 1000 <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                    ? DateTimeOffset.UnixEpoch.AddTicks(micros * TimeSpan.TicksPerMicrosecond)
                    : null,
                int.TryParse(Field(root, "PRIORITY"), NumberStyles.None, CultureInfo.InvariantCulture, out var priority) ? priority : null,
                Field(root, "_SYSTEMD_UNIT"),
                Field(root, "SYSLOG_IDENTIFIER") ?? Field(root, "_COMM"),
                Field(root, "_PID"),
                Field(root, "_HOSTNAME"),
                root.TryGetProperty("MESSAGE", out var message) && message.ValueKind == JsonValueKind.Null
                    ? "(This message is over 4 KB, so the journal doesn't send it. Read it on the host.)"
                    : AnsiCodes().Replace(Field(root, "MESSAGE") ?? string.Empty, string.Empty));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A field: text, bytes when it isn't valid UTF-8 (an array of numbers), or the first of several values (an array).</summary>
    private static string? Field(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.Number =>
                Encoding.UTF8.GetString([.. value.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Number && b.TryGetByte(out _)).Select(b => b.GetByte())]),
            JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String => value[0].GetString(),
            _ => null,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9=;_-]{1,512}$")]
    private static partial Regex CursorFormat();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[@-~]")]
    private static partial Regex AnsiCodes();
}
