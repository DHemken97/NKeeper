using System.Diagnostics;
using System.Text.Json;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// The Windows internal scan for hosts where System.Management isn't available (Linux, Docker): runs
/// <c>wmi_scan.py</c> (Impacket) as a subprocess. The login goes over its stdin, never the command line.
/// </summary>
internal static class ImpacketWmiScan
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<ScanFindings> RunAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        var python = Environment.GetEnvironmentVariable("NKEEPER_WMI_PYTHON") is { Length: > 0 } configured ? configured : "/opt/impacket/bin/python3";
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Inventory", "Scanning", "wmi_scan.py"));

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "Scanning Windows hosts needs Python with Impacket (set NKEEPER_WMI_PYTHON), or the API running on Windows.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await JsonSerializer.SerializeAsync(
                process.StandardInput.BaseStream,
                new { host = target.Address, username = target.Username, password = target.Password, domain = target.Domain },
                cancellationToken: timeout.Token);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(target, await output, process.ExitCode, await errors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new SessionException(SessionErrorCodes.Timeout, "The host didn't finish the scan in time.");
        }
    }

    private static ScanFindings Parse(SessionTarget target, string output, int exitCode, string errors)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(output).RootElement;
        }
        catch (JsonException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The WMI helper failed: {errors.Trim()}");
        }

        if (exitCode != 0 || root.TryGetProperty("error", out _))
        {
            var message = root.TryGetProperty("error", out var error) ? error.GetString() ?? "" : errors;
            var code = root.TryGetProperty("kind", out var kind) && kind.GetString() == "auth" ? SessionErrorCodes.AuthenticationFailed : SessionErrorCodes.ConnectionFailed;
            throw new SessionException(code, message);
        }

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
            hostname, ip, mac, osName, [.. ports.Select(ScanPorts.Describe)], false, users, notes, virtualization, [], [],
            WmiMapping.Cpus((long?)Number(system, "NumberOfLogicalProcessors")),
            memory,
            WmiMapping.Serial(Text(Rows(root, "bios").FirstOrDefault(), "SerialNumber"), virtualization));
    }

    /// <summary>A WMI number: Impacket gives 64-bit values (uint64 in WMI) as strings, smaller ones as numbers.</summary>
    private static ulong? Number(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number
            : value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null
            : null;

    /// <summary>A query's rows; none when the helper didn't return that query (an older wmi_scan.py, or a failed optional query).</summary>
    private static JsonElement.ArrayEnumerator Rows(JsonElement root, string name) =>
        (root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array ? rows : EmptyArray).EnumerateArray();

    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement;

    private static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
}
