using System.Globalization;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.EventLogs;

/// <summary>A classic Windows event log on a host, with how many events it holds.</summary>
public sealed record EventLogInfo(string Name, long Records);

/// <summary>One event, with its message as the host formats it.</summary>
public sealed record EventLogEntry(
    long RecordNumber,
    DateTimeOffset? TimeGenerated,
    string Level,
    int EventId,
    string? Source,
    string? Category,
    string? User,
    string? Computer,
    string Message);

/// <summary>A page of events, newest first.</summary>
/// <param name="HasMore">Whether older events matching the query remain: ask for them with the last event's record number.</param>
public sealed record EventLogPage(IReadOnlyList<EventLogEntry> Events, bool HasMore);

/// <summary>What to read from a log. The time limit isn't part of the WQL: reading stops at the first older event (see <see cref="ToWql"/>).</summary>
/// <param name="Levels">Values of <see cref="EventLogLevels"/>; empty for all.</param>
/// <param name="BeforeRecord">Only events older than this record, to page back through a log.</param>
/// <param name="Since">Stop at events older than this.</param>
internal sealed record EventLogQuery(string Log, IReadOnlyList<string> Levels, string? Source, int? EventId, long? BeforeRecord, DateTimeOffset? Since)
{
    /// <summary>How many events one request returns.</summary>
    public const int PageSize = 200;

    /// <summary>Under the proxy's 60 seconds, so the user sees why rather than a gateway error.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(50);

    public const string TimedOut = "The host didn't finish reading in time. Choose a shorter time range or fewer filters: a filter few events match searches much of the log.";

    /// <summary>The logs on a host. Windows leaves out those the login can't read, such as Security.</summary>
    public const string LogsWql = "SELECT LogfileName, NumberOfRecords FROM Win32_NTEventlogFile";

    private const string Properties = "RecordNumber, EventCode, EventType, SourceName, TimeGenerated, CategoryString, User, ComputerName, InsertionStrings, Message";

    /// <summary>
    /// The WQL for the events. Windows returns them newest first and streams matches as it finds
    /// them, so a reader can stop early; a TimeGenerated clause would instead scan the whole log.
    /// </summary>
    public string ToWql()
    {
        var clauses = new List<string> { $"Logfile = '{Escape(Log)}'" };
        if (Levels.Count > 0)
        {
            var types = Levels.SelectMany(EventLogLevels.EventTypes).Select(t => $"EventType = {t}");
            clauses.Add($"({string.Join(" OR ", types)})");
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            clauses.Add($"SourceName = '{Escape(Source.Trim())}'");
        }

        if (EventId is { } eventId)
        {
            clauses.Add(string.Create(CultureInfo.InvariantCulture, $"EventCode = {eventId}"));
        }

        if (BeforeRecord is { } before)
        {
            clauses.Add(string.Create(CultureInfo.InvariantCulture, $"RecordNumber < {before}"));
        }

        return $"SELECT {Properties} FROM Win32_NTLogEvent WHERE {string.Join(" AND ", clauses)}";
    }

    private static string Escape(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal);
}

/// <summary>The levels an event can have, as Win32_NTLogEvent's EventType reports them.</summary>
internal static class EventLogLevels
{
    public const string Error = "Error", Warning = "Warning", Information = "Information", AuditSuccess = "Audit Success", AuditFailure = "Audit Failure";

    public static bool IsKnown(string level) => level is Error or Warning or Information or AuditSuccess or AuditFailure;

    /// <summary>Critical events are reported as errors and verbose ones as information; type 0 ("success") is shown as information too.</summary>
    public static int[] EventTypes(string level) => level switch
    {
        Error => [1],
        Warning => [2],
        Information => [0, 3],
        AuditSuccess => [4],
        AuditFailure => [5],
        _ => throw new SessionException(SessionErrorCodes.BadRequest, $"“{level}” isn't an event level."),
    };

    public static string FromEventType(long? type) => type switch
    {
        1 => Error,
        2 => Warning,
        4 => AuditSuccess,
        5 => AuditFailure,
        _ => Information,
    };
}

/// <summary>Turns WMI values (from System.Management or the Impacket script) into events.</summary>
internal static class EventLogMapping
{
    /// <summary>
    /// A login typed as DOMAIN\user into the credentials prompt becomes the user and domain NTLM needs.
    /// A credential's own domain wins, and a UPN (user@domain) is left as it is.
    /// </summary>
    public static SessionTarget SplitDomain(SessionTarget target)
    {
        var slash = target.Username.IndexOf('\\', StringComparison.Ordinal);
        return slash <= 0 || slash == target.Username.Length - 1 || !string.IsNullOrEmpty(target.Domain)
            ? target
            : target with { Domain = target.Username[..slash], Username = target.Username[(slash + 1)..] };
    }

    /// <summary>A page from events read newest first: stops, without reading further, at a full page or the first event older than <paramref name="since"/>.</summary>
    public static EventLogPage Page(IEnumerable<EventLogEntry> newestFirst, DateTimeOffset? since)
    {
        var events = new List<EventLogEntry>();
        foreach (var entry in newestFirst)
        {
            if (entry.TimeGenerated < since)
            {
                return new EventLogPage(events, false);
            }

            if (events.Count == EventLogQuery.PageSize)
            {
                return new EventLogPage(events, true);
            }

            events.Add(entry);
        }

        return new EventLogPage(events, false);
    }

    public static EventLogEntry Entry(
        long recordNumber, long? eventCode, long? eventType, string? source, string? timeGenerated, string? category, string? user, string? computer, IEnumerable<string?>? insertions, string? message) =>
        new(
            recordNumber,
            ParseDmtf(timeGenerated),
            EventLogLevels.FromEventType(eventType),
            (int)(eventCode ?? 0),
            Blank(source),
            Blank(category),
            Blank(user),
            Blank(computer),
            Message(message, insertions));

    /// <summary>
    /// The host's formatted message; when it has no message file for the source, the event's raw
    /// strings, as Event Viewer falls back to.
    /// </summary>
    private static string Message(string? message, IEnumerable<string?>? insertions)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        var strings = insertions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        return strings.Count > 0 ? string.Join(Environment.NewLine, strings) : "This event has no message.";
    }

    /// <summary>A CIM datetime, e.g. <c>20261106182947.371480-000</c> (the last part is the UTC offset in minutes); null when it can't be read.</summary>
    public static DateTimeOffset? ParseDmtf(string? value)
    {
        if (value is not { Length: 25 }
            || value[14] != '.'
            || value[21] is not ('+' or '-')
            || !DateTime.TryParseExact(value[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || !int.TryParse(value[22..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 14 * 60)
        {
            return null;
        }

        return new DateTimeOffset(time, TimeSpan.FromMinutes(value[21] == '-' ? -minutes : minutes));
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Reads a Windows host's event logs with a login. Failures are <see cref="SessionException"/>s.</summary>
internal interface IEventLogReader
{
    Task<IReadOnlyList<EventLogInfo>> ListLogsAsync(SessionTarget target, CancellationToken cancellationToken);

    Task<EventLogPage> ReadAsync(SessionTarget target, EventLogQuery query, CancellationToken cancellationToken);
}
