using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.EventLogs;

/// <summary>Reads event logs with System.Management, when the NKeeper API runs on Windows.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEventLogReader : IEventLogReader
{
    private const uint RpcServerUnavailable = 0x800706BA;

    public Task<IReadOnlyList<EventLogInfo>> ListLogsAsync(SessionTarget target, CancellationToken cancellationToken) =>
        RunAsync(target, () => (IReadOnlyList<EventLogInfo>)[.. Rows(target, EventLogQuery.LogsWql).Select(row =>
        {
            using (row)
            {
                return new EventLogInfo(row["LogfileName"] as string ?? string.Empty, Number(row["NumberOfRecords"]) ?? 0);
            }
        }).Where(log => log.Name.Length > 0).OrderBy(log => log.Name, StringComparer.OrdinalIgnoreCase)], cancellationToken);

    public Task<EventLogPage> ReadAsync(SessionTarget target, EventLogQuery query, CancellationToken cancellationToken) =>
        RunAsync(target, () => EventLogMapping.Page(Rows(target, query.ToWql()).Select(ToEntry), query.Since), cancellationToken);

    private static EventLogEntry ToEntry(ManagementBaseObject row)
    {
        using (row)
        {
            return EventLogMapping.Entry(
                Number(row["RecordNumber"]) ?? 0,
                Number(row["EventCode"]),
                Number(row["EventType"]),
                row["SourceName"] as string,
                row["TimeGenerated"] as string,
                row["CategoryString"] as string,
                row["User"] as string,
                row["ComputerName"] as string,
                row["InsertionStrings"] as string[],
                row["Message"] as string);
        }
    }

    /// <summary>Yields rows as the host finds them, and leaves the rest unread when the caller stops.</summary>
    private static IEnumerable<ManagementBaseObject> Rows(SessionTarget target, string wql)
    {
        var options = WindowsInternalScan.Options(target);

        // The Security log needs SeSecurityPrivilege, which is only used when it's enabled.
        options.EnablePrivileges = true;
        var scope = WindowsInternalScan.Connect(target.Address, @"root\cimv2", options);
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery(wql), new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, BlockSize = 10 });
        using var results = searcher.Get();
        foreach (var row in results)
        {
            yield return row;
        }
    }

    private static long? Number(object? value) => value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static async Task<T> RunAsync<T>(SessionTarget target, Func<T> read, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(read, cancellationToken).WaitAsync(EventLogQuery.Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, EventLogQuery.TimedOut);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SessionException(SessionErrorCodes.AuthenticationFailed, "Windows refused the login. Check the username, password and domain, and that the account may use remote WMI (DCOM).");
        }
        catch (COMException ex) when ((uint)ex.HResult == RpcServerUnavailable)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't reach {target.Address} over RPC. Check that it is up and its firewall allows WMI (DCOM, port 135).");
        }
        catch (Exception ex) when (ex is ManagementException or COMException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The WMI query failed: {ex.Message}");
        }
    }
}
