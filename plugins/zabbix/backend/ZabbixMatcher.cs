
namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// Lines NKeeper's hosts up with Zabbix's. A stored Zabbix host ID links them for certain; for the
/// rest, a host is probably the same machine as the Zabbix host with its MAC address, else its IP
/// address, else its hostname (a DNS name or technical name, also without the domain), else its name.
/// </summary>
internal static class ZabbixMatcher
{
    /// <summary>An NKeeper host to compare, with the Zabbix host it's linked to.</summary>
    public sealed record Candidate(ComparedHost Host, string? ZabbixHostId);

    public static ZabbixComparisonResponse Compare(IReadOnlyList<Candidate> hosts, IReadOnlyList<ZabbixHostSummary> zabbixHosts)
    {
        var byId = zabbixHosts.ToDictionary(z => z.HostId, StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var linked = new List<ZabbixPair>();
        var broken = new List<ComparedHost>();
        var unlinked = new List<ComparedHost>();
        foreach (var candidate in hosts)
        {
            if (candidate.ZabbixHostId is null)
            {
                unlinked.Add(candidate.Host);
            }
            else if (byId.TryGetValue(candidate.ZabbixHostId, out var zabbix))
            {
                linked.Add(new ZabbixPair(candidate.Host, zabbix, null));
                claimed.Add(zabbix.HostId);
            }
            else
            {
                broken.Add(candidate.Host);
            }
        }

        var open = zabbixHosts.Where(z => !claimed.Contains(z.HostId)).ToList();
        var byMac = Index(open, z => z.MacAddresses);
        var byIp = Index(open, z => z.IpAddresses);
        var byHostname = Index(open, z => z.DnsNames.Append(z.Host).SelectMany(WithShortName));
        var byName = Index(open, z => [z.Name, z.Host]);

        var probable = new List<ZabbixPair>();
        var nkeeperOnly = new List<ComparedHost>();
        foreach (var host in unlinked)
        {
            var match =
                Find(byMac, [host.MacAddress], "mac", claimed)
                ?? Find(byIp, [host.IpAddress], "ip", claimed)
                ?? Find(byHostname, host.Hostname is null ? [] : WithShortName(host.Hostname), "hostname", claimed)
                ?? Find(byName, [host.Name], "name", claimed);
            if (match is { } found)
            {
                claimed.Add(found.Zabbix.HostId);
                probable.Add(new ZabbixPair(host, found.Zabbix, found.MatchedBy));
            }
            else
            {
                nkeeperOnly.Add(host);
            }
        }

        return new ZabbixComparisonResponse(
            [.. linked.OrderBy(p => p.Host.Name, StringComparer.OrdinalIgnoreCase)],
            [.. probable.OrderBy(p => p.Host.Name, StringComparer.OrdinalIgnoreCase)],
            [.. open.Where(z => !claimed.Contains(z.HostId)).OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase)],
            [.. nkeeperOnly.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)],
            [.. broken.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>A name, and its first label when it has a domain (server01.example.com and server01 match).</summary>
    private static IEnumerable<string> WithShortName(string name)
    {
        yield return name;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            yield return name[..dot];
        }
    }

    private static Dictionary<string, List<ZabbixHostSummary>> Index(IEnumerable<ZabbixHostSummary> hosts, Func<ZabbixHostSummary, IEnumerable<string>> keys)
    {
        var index = new Dictionary<string, List<ZabbixHostSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            foreach (var key in keys(host).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!index.TryGetValue(key, out var list))
                {
                    index[key] = list = [];
                }

                list.Add(host);
            }
        }

        return index;
    }

    private static (ZabbixHostSummary Zabbix, string MatchedBy)? Find(
        Dictionary<string, List<ZabbixHostSummary>> index, IEnumerable<string?> keys, string matchedBy, HashSet<string> claimed)
    {
        foreach (var key in keys)
        {
            if (key is not null && index.TryGetValue(key, out var hosts) && hosts.FirstOrDefault(h => !claimed.Contains(h.HostId)) is { } host)
            {
                return (host, matchedBy);
            }
        }

        return null;
    }
}
