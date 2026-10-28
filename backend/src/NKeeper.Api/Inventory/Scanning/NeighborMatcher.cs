using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// Matches the machines in a scanned host's neighbour (ARP/NDP) table to NKeeper hosts: each is the
/// host with its MAC address, else the one with its IP address, else — when nothing matches — a host
/// that could be added for it. This fills in the network from what the host already knows, without a
/// scan that touches every address.
/// </summary>
internal static class NeighborMatcher
{
    /// <param name="scannedHostName">The host whose table this is, named in a new host's description.</param>
    public static List<ScannedNeighbor> Match(string scannedHostName, IReadOnlyList<ScannedNeighbor> neighbors, IReadOnlyList<GuestMatcher.Candidate> hosts)
    {
        var byMac = new Dictionary<string, GuestMatcher.Candidate>(StringComparer.OrdinalIgnoreCase);
        var byIp = new Dictionary<string, GuestMatcher.Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            if (host.MacAddress is { Length: > 0 } mac)
            {
                byMac.TryAdd(mac, host);
            }

            if (host.IpAddress is { Length: > 0 } ip)
            {
                byIp.TryAdd(ip, host);
            }
        }

        var matched = new List<ScannedNeighbor>(neighbors.Count);
        foreach (var neighbor in neighbors)
        {
            var (host, matchedBy) =
                neighbor.MacAddress is { } mac && byMac.TryGetValue(mac, out var byMacHost) ? (byMacHost, "mac")
                : byIp.TryGetValue(neighbor.IpAddress, out var byIpHost) ? (byIpHost, "ip")
                : (null, null);

            matched.Add(host is not null
                ? neighbor with { HostId = host.Id, HostName = host.Name, MatchedBy = matchedBy }
                : neighbor with { NewHost = Propose(neighbor, scannedHostName) });
        }

        return matched;
    }

    /// <summary>The host to add for an unknown neighbour: named by its address, with its IP and MAC.</summary>
    private static ProposedHost Propose(ScannedNeighbor neighbor, string scannedHostName) =>
        new(neighbor.IpAddress, null, neighbor.IpAddress, neighbor.MacAddress, $"Seen in {scannedHostName}’s neighbour table.");
}
