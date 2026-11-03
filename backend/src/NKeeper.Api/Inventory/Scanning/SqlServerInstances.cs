using System.Globalization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// A SQL Server instance an internal scan found on the host: from its registry on Windows (instance
/// names and their TCP settings), from <c>sqlservr</c> and <c>mssql.conf</c> on Linux.
/// </summary>
/// <param name="Name">The instance name; MSSQLSERVER for the default instance.</param>
/// <param name="StaticPort">The port it's set to listen on (IPAll TcpPort, or mssql.conf's tcpport).</param>
/// <param name="DynamicPort">The port it chose for itself at its last start (IPAll TcpDynamicPorts): it can change at a restart.</param>
/// <param name="TcpEnabled">Whether TCP/IP is turned on for it (NKeeper connects over TCP only).</param>
internal sealed record FoundSqlInstance(string Name, int? StaticPort, int? DynamicPort, bool TcpEnabled);

/// <summary>Turns the SQL Server instances a scan found into services to offer, each with its instance and port.</summary>
internal static class SqlServerInstances
{
    /// <summary>
    /// The scanned ports with each instance's port named for it and offered as an MS SQL service (its
    /// instance, and the port), and notes for instances that can't be connected to as they are.
    /// </summary>
    public static IReadOnlyList<ScannedPort> Apply(IEnumerable<ScannedPort> ports, IEnumerable<FoundSqlInstance> instances, List<string> notes)
    {
        var byPort = ports.ToDictionary(p => p.Port);
        foreach (var instance in instances)
        {
            var named = SqlServerInstance.Normalize(instance.Name);
            var label = named is null ? "SQL Server (default instance)" : $"SQL Server ({named})";
            if (!instance.TcpEnabled)
            {
                notes.Add($"{label} has TCP/IP turned off, so NKeeper can't connect to it. Turn it on in SQL Server Configuration Manager, then scan again.");
                continue;
            }

            if ((instance.StaticPort ?? instance.DynamicPort) is not { } port)
            {
                notes.Add($"{label} has no TCP port yet (it isn't running, or hasn't listened since TCP/IP was turned on).");
                continue;
            }

            if (instance.StaticPort is null)
            {
                notes.Add($"{label} uses a dynamic port ({port} now), which can change when it restarts. Give it a static port in SQL Server Configuration Manager, or remove the port from the service so the SQL Server Browser finds it.");
            }

            byPort[port] = new ScannedPort(port, label, ServiceProtocol.Ssms, named);
        }

        return [.. byPort.Values.OrderBy(p => p.Port)];
    }

    /// <summary>A registry port value: "1433", "" or "0" (none), or a list like "1433,1434" (the first).</summary>
    public static int? Port(string? value)
    {
        var first = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;
    }

    /// <summary>
    /// SQL Server on Linux (one instance): the port <c>sqlservr</c> listens on, preferring the one in
    /// <c>mssql.conf</c>, then 1433. Null when SQL Server isn't there.
    /// </summary>
    /// <param name="processPorts">Ports <c>sqlservr</c> listens on, from <c>ss -p</c> (needs root to see another user's process).</param>
    /// <param name="configuredPort">mssql.conf's <c>network.tcpport</c>, when set.</param>
    public static FoundSqlInstance? OnLinux(IReadOnlyCollection<int> processPorts, int? configuredPort, bool installed, IReadOnlyCollection<int> listening, List<string> notes)
    {
        if (!installed && processPorts.Count == 0)
        {
            return null;
        }

        var port = configuredPort ?? (processPorts.Contains(1433) ? 1433 : processPorts.Count > 0 ? processPorts.Min() : 1433);
        if (!listening.Contains(port) && !processPorts.Contains(port))
        {
            notes.Add($"SQL Server is installed but isn't listening on port {port} (is it running?).");
        }

        return new FoundSqlInstance(SqlServerInstance.DefaultName, port, null, true);
    }
}
