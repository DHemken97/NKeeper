using System.Globalization;
using System.Text.Json;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.EventLogs;

/// <summary>Reads event logs where System.Management isn't available (Linux, Docker), with <c>wmi_event_log.py</c> (Impacket).</summary>
internal sealed class ImpacketEventLogReader : IEventLogReader
{
    public async Task<IReadOnlyList<EventLogInfo>> ListLogsAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(target, EventLogQuery.LogsWql, null, null, cancellationToken);
        return [.. rows
            .Select(row => new EventLogInfo(ImpacketWmiScan.Text(row, "LogfileName") ?? string.Empty, (long)(ImpacketWmiScan.Number(row, "NumberOfRecords") ?? 0)))
            .Where(log => log.Name.Length > 0)
            .OrderBy(log => log.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<EventLogPage> ReadAsync(SessionTarget target, EventLogQuery query, CancellationToken cancellationToken)
    {
        // One more than a page, to tell whether older events remain.
        var rows = await QueryAsync(target, query.ToWql(), EventLogQuery.PageSize + 1, query.Since, cancellationToken);
        return EventLogMapping.Page(rows.Select(ToEntry), query.Since);
    }

    private static EventLogEntry ToEntry(JsonElement row) =>
        EventLogMapping.Entry(
            (long)(ImpacketWmiScan.Number(row, "RecordNumber") ?? 0),
            (long?)ImpacketWmiScan.Number(row, "EventCode"),
            (long?)ImpacketWmiScan.Number(row, "EventType"),
            ImpacketWmiScan.Text(row, "SourceName"),
            ImpacketWmiScan.Text(row, "TimeGenerated"),
            ImpacketWmiScan.Text(row, "CategoryString"),
            ImpacketWmiScan.Text(row, "User"),
            ImpacketWmiScan.Text(row, "ComputerName"),
            row.TryGetProperty("InsertionStrings", out var strings) && strings.ValueKind == JsonValueKind.Array
                ? strings.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null)
                : null,
            ImpacketWmiScan.Text(row, "Message"));

    private static async Task<List<JsonElement>> QueryAsync(SessionTarget target, string wql, int? limit, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var root = await ImpacketHelper.RunAsync(
            Path.Combine("EventLogs", "wmi_event_log.py"),
            new
            {
                host = target.Address,
                username = target.Username,
                password = target.Password,
                domain = target.Domain,
                query = wql,
                limit,
                since = since?.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            },
            EventLogQuery.Timeout,
            "Reading Windows event logs needs Python with Impacket (set NKEEPER_WMI_PYTHON), or the API running on Windows.",
            EventLogQuery.TimedOut,
            cancellationToken);
        return [.. root.GetProperty("rows").EnumerateArray()];
    }
}
