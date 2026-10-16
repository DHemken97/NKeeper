using NKeeper.Api.Contracts.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>Turns WMI values (from System.Management or the Impacket script) into scan findings.</summary>
internal static class WmiMapping
{
    public static string? Virtualization(string? maker, string? model) => (maker, model) switch
    {
        (_, { } m) when m.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase) => "hyperv",
        (_, { } m) when m.Contains("VMware", StringComparison.OrdinalIgnoreCase) => "vmware",
        (_, { } m) when m.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) => "oracle",
        ({ } k, _) when k.Contains("QEMU", StringComparison.OrdinalIgnoreCase) => "kvm",
        ({ } k, _) when k.Contains("Xen", StringComparison.OrdinalIgnoreCase) => "xen",
        _ => null,
    };

    /// <summary>The adapter that holds the address NKeeper reached, or the only enabled one.</summary>
    public static (string? Ip, string? Mac) FindAddress(IReadOnlyList<(string[] Ips, string? Mac)> adapters, string reachedAt, List<string> notes)
    {
        var match = adapters.FirstOrDefault(a => a.Ips.Contains(reachedAt, StringComparer.OrdinalIgnoreCase));
        if (match.Mac is null && adapters.Count == 1)
        {
            match = adapters[0];
        }

        if (match.Mac is null)
        {
            notes.Add("The host has several network adapters and the one NKeeper reached couldn't be told apart, so no MAC address was picked.");
        }

        var ip = System.Net.IPAddress.TryParse(reachedAt, out _) ? reachedAt : match.Ips?.FirstOrDefault();
        return (ip, match.Mac is not null && NetworkAddress.TryNormalizeMac(match.Mac, out var normalized) ? normalized : null);
    }

    public static bool IsLoopback(string? address) => address is null || address.StartsWith("127.", StringComparison.Ordinal) || address == "::1";

    public static int? Rid(string? sid) => sid is not null && int.TryParse(sid[(sid.LastIndexOf('-') + 1)..], out var rid) ? rid : null;
}
