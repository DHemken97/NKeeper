using System.Globalization;
using System.Text.Json;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Plugins.Zabbix;

/// <summary>A Zabbix host with what NKeeper needs to add it as a host as well as list it.</summary>
internal sealed record ZabbixHostRecord(ZabbixHostSummary Summary, string? Description, IReadOnlyList<string> Groups, string? SerialNumber);

/// <summary>The Zabbix API calls NKeeper makes (host.get, hostinterface.get, item.get, problem.get), read into contracts.</summary>
internal static class ZabbixQueries
{
    /// <summary>Items worth showing when a host has them, from Zabbix's own templates (Linux, Windows, generic agent and ICMP).</summary>
    public static readonly string[] WellKnownItemKeys =
    [
        "system.hostname",
        "system.uname",
        "system.sw.os",
        "system.sw.arch",
        "system.uptime",
        "system.cpu.num",
        "system.cpu.util",
        "system.cpu.load[all,avg1]",
        "vm.memory.size[total]",
        "vm.memory.size[available]",
        "vm.memory.size[pavailable]",
        "vm.memory.utilization",
        "vm.memory.util",
        "system.swap.size[,pfree]",
        "vfs.fs.dependent.size[/,pused]",
        "vfs.fs.size[/,pused]",
        "vfs.fs.dependent.size[C:,pused]",
        "vfs.fs.size[C:,pused]",
        "agent.version",
        "agent.ping",
        "icmpping",
        "icmppingsec",
        "icmppingloss",
    ];

    private static readonly string[] HostOutput = ["hostid", "host", "name", "status", "description", "maintenance_status"];
    private static readonly string[] InterfaceOutput = ["type", "ip", "dns", "port", "useip", "main", "available", "error"];
    private static readonly string[] SummaryInventory = ["os", "macaddress_a", "macaddress_b", "serialno_a"];
    private static readonly string[] TagOutput = ["tag", "value"];
    private static readonly string[] GroupOutput = ["name"];
    private static readonly string[] IdOutput = ["hostid"];
    private static readonly string[] ItemOutput = ["key_", "name", "lastvalue", "units", "lastclock"];
    private static readonly string[] ProblemOutput = ["eventid", "name", "severity", "clock", "acknowledged", "suppressed"];
    private static readonly string[] ProblemSort = ["eventid"];

    /// <summary>How many hosts the token can see (to test the settings).</summary>
    public static async Task<int> CountHostsAsync(ZabbixClient client, CancellationToken cancellationToken)
    {
        var result = await client.CallAsync("host.get", new { countOutput = true }, cancellationToken);
        return int.TryParse(Text(result), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>Every host the token can see, with its interfaces, groups, tags and a few inventory fields.</summary>
    public static async Task<List<ZabbixHostRecord>> ListHostsAsync(ZabbixClient client, CancellationToken cancellationToken, IReadOnlyCollection<string>? hostIds = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["output"] = HostOutput,
            ["selectInterfaces"] = InterfaceOutput,
            ["selectInventory"] = SummaryInventory,
            ["selectTags"] = TagOutput,
            ["selectHostGroups"] = GroupOutput,
            ["sortfield"] = "name",
        };
        if (hostIds is not null)
        {
            parameters["hostids"] = hostIds;
        }

        var result = await client.CallAsync("host.get", parameters, cancellationToken);
        return [.. Array(result).Select(ToRecord)];
    }

    /// <summary>
    /// Hosts whose visible or technical name, IP address or DNS name contains the text (at most
    /// <paramref name="limit"/>), to choose one to link.
    /// </summary>
    public static async Task<List<ZabbixHostSummary>> SearchAsync(ZabbixClient client, string text, int limit, CancellationToken cancellationToken)
    {
        var byName = await client.CallAsync("host.get", new
        {
            output = IdOutput,
            search = new { name = text, host = text },
            searchByAny = true,
            limit,
        }, cancellationToken);
        var byInterface = await client.CallAsync("hostinterface.get", new
        {
            output = IdOutput,
            search = new { ip = text, dns = text },
            searchByAny = true,
            limit,
        }, cancellationToken);

        var ids = Array(byName).Concat(Array(byInterface)).Select(h => String(h, "hostid")).OfType<string>().Distinct().Take(limit).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return [.. (await ListHostsAsync(client, cancellationToken, ids)).Select(r => r.Summary)];
    }

    /// <summary>One host's details, latest well-known values and unresolved problems.</summary>
    /// <exception cref="SessionException">With <see cref="ZabbixErrorCodes.HostNotFound"/> when the token can't see a host with that ID.</exception>
    public static async Task<ZabbixHostOverview> GetOverviewAsync(ZabbixClient client, string hostId, CancellationToken cancellationToken)
    {
        var hosts = await client.CallAsync("host.get", new
        {
            hostids = new[] { hostId },
            output = HostOutput,
            selectInterfaces = InterfaceOutput,
            selectInventory = "extend",
            selectTags = TagOutput,
            selectHostGroups = GroupOutput,
        }, cancellationToken);
        var host = Array(hosts).FirstOrDefault();
        if (host.ValueKind != JsonValueKind.Object)
        {
            throw new SessionException(ZabbixErrorCodes.HostNotFound, $"Zabbix has no host with ID {hostId}, or the API token can't see it.");
        }

        var items = await client.CallAsync("item.get", new
        {
            hostids = new[] { hostId },
            output = ItemOutput,
            filter = new { key_ = WellKnownItemKeys },
            webitems = true,
        }, cancellationToken);
        var problems = await client.CallAsync("problem.get", new
        {
            hostids = new[] { hostId },
            output = ProblemOutput,
            sortfield = ProblemSort,
            sortorder = "DESC",
            limit = 100,
        }, cancellationToken);

        var record = ToRecord(host);
        var interfaces = Array(host, "interfaces").Select(ToInterface).ToList();
        return new ZabbixHostOverview(
            record.Summary,
            record.Description,
            String(host, "maintenance_status") == "1",
            OverallAvailability(interfaces),
            interfaces,
            record.Groups,
            Inventory(host),
            [.. Array(items)
                .Select(i => new ZabbixItemResponse(
                    String(i, "key_") ?? string.Empty,
                    String(i, "name") ?? string.Empty,
                    String(i, "lastvalue"),
                    String(i, "units"),
                    UnixTime(String(i, "lastclock"))))
                // Never collected: lastclock is 0.
                .Where(i => i.CheckedAtUtc is not null)
                .OrderBy(i => System.Array.IndexOf(WellKnownItemKeys, i.Key))],
            [.. Array(problems).Select(p => new ZabbixProblemResponse(
                String(p, "eventid") ?? string.Empty,
                String(p, "name") ?? string.Empty,
                int.TryParse(String(p, "severity"), CultureInfo.InvariantCulture, out var severity) ? severity : 0,
                UnixTime(String(p, "clock")) ?? DateTimeOffset.UnixEpoch,
                String(p, "acknowledged") == "1",
                String(p, "suppressed") == "1"))]);
    }

    private static ZabbixHostRecord ToRecord(JsonElement host)
    {
        var interfaces = Array(host, "interfaces").ToList();
        var inventory = host.TryGetProperty("inventory", out var inv) && inv.ValueKind == JsonValueKind.Object ? inv : default;
        var macs = new List<string>();
        foreach (var field in new[] { "macaddress_a", "macaddress_b" })
        {
            // Inventory fields are free text; keep what reads as a MAC address.
            if (inventory.ValueKind == JsonValueKind.Object
                && NetworkAddress.TryNormalizeMac(String(inventory, field) ?? string.Empty, out var mac)
                && !macs.Contains(mac))
            {
                macs.Add(mac);
            }
        }

        var summary = new ZabbixHostSummary(
            String(host, "hostid") ?? string.Empty,
            String(host, "host") ?? string.Empty,
            String(host, "name") ?? String(host, "host") ?? string.Empty,
            String(host, "status") == "0",
            [.. interfaces
                // The main interface first.
                .OrderByDescending(i => String(i, "main") == "1")
                .Select(i => NetworkAddress.TryNormalizeIp(String(i, "ip") ?? string.Empty, out var ip) ? ip : null)
                .OfType<string>()
                .Where(ip => ip is not "127.0.0.1" and not "::1")
                .Distinct()],
            [.. interfaces
                .OrderByDescending(i => String(i, "main") == "1")
                .Select(i => InventoryText.TrimToNull(String(i, "dns")))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            macs,
            inventory.ValueKind == JsonValueKind.Object ? InventoryText.TrimToNull(String(inventory, "os")) : null,
            [.. Array(host, "tags").Select(t => new HostTagResponse(String(t, "tag") ?? string.Empty, InventoryText.TrimToNull(String(t, "value"))))]);
        return new ZabbixHostRecord(
            summary,
            InventoryText.TrimToNull(String(host, "description")),
            [.. Array(host, "hostgroups").Select(g => String(g, "name")).OfType<string>()],
            inventory.ValueKind == JsonValueKind.Object ? InventoryText.TrimToNull(String(inventory, "serialno_a")) : null);
    }

    private static ZabbixInterfaceResponse ToInterface(JsonElement item) => new(
        String(item, "type") switch { "1" => "Agent", "2" => "SNMP", "3" => "IPMI", "4" => "JMX", var other => other ?? "Unknown" },
        InventoryText.TrimToNull(String(item, "ip")),
        InventoryText.TrimToNull(String(item, "dns")),
        String(item, "port") ?? string.Empty,
        String(item, "useip") == "1",
        String(item, "main") == "1",
        String(item, "available") switch { "1" => "Available", "2" => "Unavailable", _ => "Unknown" },
        InventoryText.TrimToNull(String(item, "error")));

    /// <summary>Available when every interface with a known state is, Unavailable when every one isn't, Mixed in between.</summary>
    private static string OverallAvailability(IReadOnlyList<ZabbixInterfaceResponse> interfaces)
    {
        var known = interfaces.Where(i => i.Availability != "Unknown").Select(i => i.Availability).Distinct().ToList();
        return known.Count switch
        {
            0 => "Unknown",
            1 => known[0],
            _ => "Mixed",
        };
    }

    /// <summary>The inventory fields with a value; Zabbix answers [] instead of an object when inventory is off.</summary>
    private static Dictionary<string, string> Inventory(JsonElement host)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (host.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in inventory.EnumerateObject())
            {
                if (property.Name is not ("hostid" or "inventory_mode")
                    && property.Value.ValueKind == JsonValueKind.String
                    && InventoryText.TrimToNull(property.Value.GetString()) is { } value)
                {
                    fields[property.Name] = value;
                }
            }
        }

        return fields;
    }

    private static DateTimeOffset? UnixTime(string? seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? Array(value) : [];

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? Text(value) : null;

    /// <summary>Zabbix sends numbers as strings, but take either.</summary>
    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };
}
