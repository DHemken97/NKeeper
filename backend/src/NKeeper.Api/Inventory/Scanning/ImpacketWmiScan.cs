using System.Text.Json;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// The Windows internal scan for hosts where System.Management isn't available (Linux, Docker): runs
/// <c>wmi_scan.py</c> (Impacket) as a subprocess. The login goes over its stdin, never the command line.
/// </summary>
internal static class ImpacketWmiScan
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<ScanFindings> RunAsync(SessionTarget target, CancellationToken cancellationToken) =>
        Parse(target, await ImpacketHelper.RunAsync(
            Path.Combine("Inventory", "Scanning", "wmi_scan.py"),
            new { host = target.Address, username = target.Username, password = target.Password, domain = target.Domain },
            Timeout,
            "Scanning Windows hosts needs Python with Impacket (set NKEEPER_WMI_PYTHON), or the API running on Windows.",
            "The host didn't finish the scan in time.",
            cancellationToken));

    private static ScanFindings Parse(SessionTarget target, JsonElement root)
    {
        var notes = root.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        var system = Rows(root, "system").FirstOrDefault();
        var os = Rows(root, "os").FirstOrDefault();
        var hostname = Text(system, "DNSHostName") ?? Text(system, "Name");
        var osName = os.ValueKind == JsonValueKind.Object ? $"{Text(os, "Caption")} {Text(os, "Version")}".Trim() : null;

        var adapters = Rows(root, "adapters")
            .Select(a => (Ips: a.TryGetProperty("IPAddress", out var ips) && ips.ValueKind == JsonValueKind.Array ? ips.EnumerateArray().Select(i => i.GetString()!).ToArray() : [], Mac: Text(a, "MACAddress")))
            .ToList();
        var (ip, mac) = WmiMapping.FindAddress(adapters, target.Address, notes);

        var ports = Rows(root, "ports")
            .Where(p => !WmiMapping.IsLoopback(Text(p, "LocalAddress")) && p.TryGetProperty("LocalPort", out var port) && port.ValueKind == JsonValueKind.Number)
            .Select(p => p.GetProperty("LocalPort").GetInt32())
            .Distinct().Order().ToList();

        var users = new List<ScannedUser>();
        foreach (var account in Rows(root, "users"))
        {
            if (Text(account, "Name") is { } name && WmiMapping.Rid(Text(account, "SID")) is { } rid)
            {
                users.Add(new ScannedUser(name, rid, Text(account, "FullName"), "Windows"));
            }
        }

        var virtualization = WmiMapping.Virtualization(Text(system, "Manufacturer"), Text(system, "Model"));
        var memory = WmiMapping.MemoryMiB(Rows(root, "memory").Select(m => Number(m, "Capacity")).OfType<ulong>(), Number(system, "TotalPhysicalMemory"));
        return new ScanFindings(
            hostname, ip, mac, osName, SqlServerInstances.Apply(ports.Select(ScanPorts.Describe), SqlInstances(root), notes), false, users, notes, virtualization, [], [],
            WmiMapping.Cpus((long?)Number(system, "NumberOfLogicalProcessors")),
            memory,
            WmiMapping.Serial(Text(Rows(root, "bios").FirstOrDefault(), "SerialNumber"), virtualization));
    }

    /// <summary>The SQL Server instances the helper read from the registry (none from an older wmi_scan.py).</summary>
    private static List<FoundSqlInstance> SqlInstances(JsonElement root) =>
        [.. Rows(root, "sqlInstances")
            .Where(i => Text(i, "name") is not null)
            .Select(i => new FoundSqlInstance(
                Text(i, "name")!,
                SqlServerInstances.Port(Text(i, "tcpPort")),
                SqlServerInstances.Port(Text(i, "dynamicPort")),
                Number(i, "enabled") is not 0))];

    /// <summary>A WMI number: Impacket gives 64-bit values (uint64 in WMI) as strings, smaller ones as numbers.</summary>
    internal static ulong? Number(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number
            : value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null
            : null;

    /// <summary>A query's rows; none when the helper didn't return that query (an older wmi_scan.py, or a failed optional query).</summary>
    private static JsonElement.ArrayEnumerator Rows(JsonElement root, string name) =>
        (root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array ? rows : EmptyArray).EnumerateArray();

    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement;

    internal static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
}
