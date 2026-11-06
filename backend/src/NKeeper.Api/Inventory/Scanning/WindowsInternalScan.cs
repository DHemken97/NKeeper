using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// The internal scan for Windows hosts: read-only WMI queries over DCOM (RPC on port 135 plus its
/// dynamic ports) with the given login. It reads the host's own state and probes nothing else.
/// Needs the NKeeper API to run on Windows, because it uses System.Management.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInternalScan
{
    private const uint RpcServerUnavailable = 0x800706BA;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static async Task<ScanFindings> RunAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Query(target), cancellationToken).WaitAsync(Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, "The host didn't finish the scan in time.");
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

    /// <summary>How to sign in to WMI on the target with its login.</summary>
    internal static ConnectionOptions Options(SessionTarget target)
    {
        var options = new ConnectionOptions
        {
            Username = target.Username,
            Password = target.Password,
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = Timeout,
        };
        if (target.Domain is { Length: > 0 } domain)
        {
            options.Authority = $"ntlmdomain:{domain}";
        }

        return options;
    }

    private static ScanFindings Query(SessionTarget target)
    {
        var options = Options(target);
        var notes = new List<string>();
        var cimv2 = Connect(target.Address, @"root\cimv2", options);

        var system = Select(cimv2, "SELECT DNSHostName, Name, Manufacturer, Model, NumberOfLogicalProcessors, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Select(cimv2, "SELECT Caption, Version FROM Win32_OperatingSystem").FirstOrDefault();
        var hostname = Text(system, "DNSHostName") ?? Text(system, "Name");
        var osName = os is null ? null : $"{Text(os, "Caption")} {Text(os, "Version")}".Trim();

        var (ip, mac) = FindAddress(cimv2, target.Address, notes);

        var ports = new SortedSet<int>();
        try
        {
            // State 2 is "Listen"; MSFT_NetTCPConnection needs Windows 8 / Server 2012 or later.
            foreach (var connection in Select(Connect(target.Address, @"root\StandardCimv2", options), "SELECT LocalAddress, LocalPort FROM MSFT_NetTCPConnection WHERE State = 2"))
            {
                if (Text(connection, "LocalAddress") is { } address && !address.StartsWith("127.", StringComparison.Ordinal) && address != "::1")
                {
                    ports.Add(Convert.ToInt32(connection["LocalPort"], System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        catch (ManagementException)
        {
            notes.Add("The listening ports couldn't be read (this needs Windows 8 or Server 2012 or later).");
        }

        IReadOnlyList<FoundSqlInstance> sqlInstances = [];
        try
        {
            sqlInstances = SqlInstances(Connect(target.Address, @"root\default", options));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            notes.Add($"SQL Server instances couldn't be read from the registry ({ex.Message}).");
        }

        var users = new List<ScannedUser>();
        foreach (var account in Select(cimv2, "SELECT Name, FullName, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE AND Disabled = FALSE AND Lockout = FALSE"))
        {
            var sid = Text(account, "SID");
            if (Text(account, "Name") is { } name && sid is not null && int.TryParse(sid[(sid.LastIndexOf('-') + 1)..], out var rid))
            {
                users.Add(new ScannedUser(name, rid, Text(account, "FullName"), "Windows"));
            }
        }

        var virtualization = Virtualization(system);
        var modules = Select(cimv2, "SELECT Capacity FROM Win32_PhysicalMemory").Select(m => m["Capacity"] is ulong capacity ? capacity : 0UL);
        var bios = Select(cimv2, "SELECT SerialNumber FROM Win32_BIOS").FirstOrDefault();
        return new ScanFindings(
            hostname, ip, mac, osName, SqlServerInstances.Apply(ports.Select(ScanPorts.Describe), sqlInstances, notes), false, users, notes, virtualization, [], [],
            WmiMapping.Cpus(system?["NumberOfLogicalProcessors"] is uint cpus ? cpus : null),
            WmiMapping.MemoryMiB(modules, system?["TotalPhysicalMemory"] is ulong total ? total : null),
            WmiMapping.Serial(Text(bios, "SerialNumber"), virtualization));
    }

    private const uint LocalMachine = 0x80000002;
    private const string SqlServerKey = @"SOFTWARE\Microsoft\Microsoft SQL Server";

    /// <summary>
    /// SQL Server's instances from its registry (StdRegProv, read only): each name under
    /// Instance Names\SQL, and its TCP settings (enabled; IPAll's static and dynamic ports).
    /// </summary>
    private static List<FoundSqlInstance> SqlInstances(ManagementScope scope)
    {
        using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
        string[]? Names(string key)
        {
            using var input = registry.GetMethodParameters("EnumValues");
            input["hDefKey"] = LocalMachine;
            input["sSubKeyName"] = key;
            using var output = registry.InvokeMethod("EnumValues", input, null);
            return output?["sNames"] as string[];
        }

        string? Text(string key, string name)
        {
            using var input = registry.GetMethodParameters("GetStringValue");
            input["hDefKey"] = LocalMachine;
            input["sSubKeyName"] = key;
            input["sValueName"] = name;
            using var output = registry.InvokeMethod("GetStringValue", input, null);
            return output?["sValue"] as string;
        }

        uint? Number(string key, string name)
        {
            using var input = registry.GetMethodParameters("GetDWORDValue");
            input["hDefKey"] = LocalMachine;
            input["sSubKeyName"] = key;
            input["sValueName"] = name;
            using var output = registry.InvokeMethod("GetDWORDValue", input, null);
            return output?["uValue"] as uint?;
        }

        var instances = new List<FoundSqlInstance>();
        var namesKey = $@"{SqlServerKey}\Instance Names\SQL";
        foreach (var name in Names(namesKey) ?? [])
        {
            if (Text(namesKey, name) is not { Length: > 0 } id)
            {
                continue;
            }

            var tcp = $@"{SqlServerKey}\{id}\MSSQLServer\SuperSocketNetLib\Tcp";
            instances.Add(new FoundSqlInstance(
                name,
                SqlServerInstances.Port(Text($@"{tcp}\IPAll", "TcpPort")),
                SqlServerInstances.Port(Text($@"{tcp}\IPAll", "TcpDynamicPorts")),
                Number(tcp, "Enabled") is not 0));
        }

        return instances;
    }

    internal static ManagementScope Connect(string address, string path, ConnectionOptions options)
    {
        var host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        var scope = new ManagementScope($@"\\{host}\{path}", options);
        scope.Connect();
        return scope;
    }

    private static IEnumerable<ManagementObject> Select(ManagementScope scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
        return [.. searcher.Get().Cast<ManagementObject>()];
    }

    private static (string? Ip, string? Mac) FindAddress(ManagementScope scope, string reachedAt, List<string> notes) =>
        WmiMapping.FindAddress(
            [.. Select(scope, "SELECT IPAddress, MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
                .Select(a => (Ips: a["IPAddress"] as string[] ?? [], Mac: Text(a, "MACAddress")))],
            reachedAt,
            notes);

    private static string? Virtualization(ManagementObject? system) => WmiMapping.Virtualization(Text(system, "Manufacturer"), Text(system, "Model"));
    private static string? Text(ManagementBaseObject? item, string property) =>
        item?[property] is string { Length: > 0 } value ? value.Trim() : null;
}
