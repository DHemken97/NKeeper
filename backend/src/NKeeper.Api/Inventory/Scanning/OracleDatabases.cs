using System.Text.RegularExpressions;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// Oracle databases an internal scan found on the host, from what it can read there: <c>/etc/oratab</c>, the
/// running instances (<c>ora_pmon_&lt;SID&gt;</c>), the ports the listener (<c>tnslsnr</c>) listens on, and the
/// services the listener says it offers (<c>lsnrctl status</c>, when the scanning login may run it).
/// </summary>
internal static partial class OracleDatabases
{
    public const int DefaultPort = 1521;

    /// <summary>
    /// The scanned ports with the listener's port offered once per database (its service name), and a note
    /// when names had to come from the instances (SIDs) or the listener isn't listening.
    /// </summary>
    /// <param name="lines">The scan's "oracle" section: "oratab SID HOME", "pmon SID", "listen ADDRESS:PORT", "service NAME".</param>
    /// <param name="listenPorts">Non-loopback ports tnslsnr listens on.</param>
    public static IReadOnlyList<ScannedPort> Apply(IReadOnlyList<ScannedPort> ports, IReadOnlyList<string> lines, IReadOnlyCollection<int> listenPorts, List<string> notes)
    {
        var sids = lines
            .Where(l => l.StartsWith("pmon ", StringComparison.Ordinal) || l.StartsWith("oratab ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1))
            .OfType<string>()
            .Where(IsName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var services = lines
            .Where(l => l.StartsWith("service ", StringComparison.Ordinal))
            .Select(l => l[8..].Trim())
            .Where(s => IsName(s) && !IsInternalService(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sids.Count == 0 && services.Count == 0)
        {
            return ports;
        }

        var listener = listenPorts.Count > 0 ? listenPorts.ToList()
            : ports.Any(p => p.Port == DefaultPort) ? [DefaultPort]
            : [];
        if (listener.Count == 0)
        {
            notes.Add($"Oracle is installed ({string.Join(", ", sids)}), but its listener isn't listening on a port this scan can see (is it running?).");
            return ports;
        }

        var names = services.Count > 0 ? services : sids;
        if (services.Count == 0)
        {
            notes.Add(
                $"The listener's service names couldn't be read (lsnrctl needs the oracle user, or its environment), so Oracle's instances are offered by SID ({string.Join(", ", sids)}). " +
                "NKeeper connects by SID when no service has that name; a pluggable database needs its own service name.");
        }

        var result = ports.Where(p => !(listener.Contains(p.Port) && (p.Protocol == ServiceProtocol.Oracle || p.Protocol is null))).ToList();
        foreach (var port in listener)
        {
            result.AddRange(names.Select(name => new ScannedPort(port, $"Oracle ({name})", ServiceProtocol.Oracle, name)));
        }

        return [.. result.OrderBy(p => p.Port).ThenBy(p => p.Instance, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Services every Oracle listener has that aren't a database to sign in to: the XML DB dispatchers (…XDB),
    /// external procedures, internal SYS$ services and a pluggable database's GUID-named one.
    /// </summary>
    internal static bool IsInternalService(string name) =>
        name.EndsWith("XDB", StringComparison.OrdinalIgnoreCase)
        || name.Contains("XDB.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SYS$", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PLSExtProc", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLRExtProc", StringComparison.OrdinalIgnoreCase)
        || Guid().IsMatch(name);

    private static bool IsName(string name) => SqlServerInstance.IsValidOracleService(name) && name.Length <= HostService.InstanceMaxLength;

    /// <summary>A pluggable database's internal service: 32 hex digits, maybe with the domain.</summary>
    [GeneratedRegex(@"^[0-9A-Fa-f]{32}(\..*)?$")]
    private static partial Regex Guid();
}
