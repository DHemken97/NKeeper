using NKeeper.Api.Contracts.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// Matches the other nodes of a scanned Proxmox node's cluster to NKeeper hosts, and proposes hosts
/// for the ones NKeeper doesn't have, so a cluster is added from a scan of any one node.
/// </summary>
internal static class ClusterNodeMatcher
{
    /// <summary>
    /// A node is the host with its MAC address, else its IP address (or hostname, when corosync
    /// reaches it by name), else its name; each only when exactly one host fits. Node names repeat
    /// across sites (every site's "pve1"), so a name never matches a host whose MAC or IP address differs.
    /// </summary>
    /// <param name="scannedHostName">The scanned node, named in a new host's description.</param>
    /// <param name="tagged">The hosts that already have the cluster's tag.</param>
    public static List<ScannedClusterNode> Match(
        Guid scannedHostId, string scannedHostName, IReadOnlyList<ScannedClusterNode> nodes, IReadOnlyList<GuestMatcher.Candidate> hosts, IReadOnlySet<Guid> tagged)
    {
        var others = hosts.Where(h => h.Id != scannedHostId).ToList();
        var matched = new List<ScannedClusterNode>(nodes.Count);
        foreach (var node in nodes)
        {
            var ip = node.Address is { } address && NetworkAddress.TryNormalizeIp(address, out var normalized) ? normalized : null;
            var hostname = ip is null ? node.Address : null;
            var named = others.Where(h => GuestMatcher.HasName(h, node.Name)).ToList();
            var plausible = named.Where(h => !HasOther(h.MacAddress, node.MacAddress) && !(ip is not null && HasOther(h.IpAddress, ip))).ToList();

            var ambiguous = false;
            GuestMatcher.Candidate? host = null;
            string? matchedBy = null;
            foreach (var (by, matches) in new (string, IEnumerable<GuestMatcher.Candidate>)[]
            {
                ("mac", others.Where(h => node.MacAddress is not null && string.Equals(h.MacAddress, node.MacAddress, StringComparison.OrdinalIgnoreCase))),
                ("ip", others.Where(h => ip is not null && h.IpAddress == ip)),
                ("hostname", others.Where(h => hostname is not null && string.Equals(h.Hostname, hostname, StringComparison.OrdinalIgnoreCase))),
                ("name", plausible),
            })
            {
                var list = matches.Take(2).ToList();
                if (list.Count == 1)
                {
                    (host, matchedBy) = (list[0], by);
                    break;
                }

                ambiguous |= list.Count > 1;
            }

            if (host is not null)
            {
                matched.Add(node with { HostId = host.Id, HostName = host.Name, MatchedBy = matchedBy, TagProposed = host.CanManage && !tagged.Contains(host.Id) });
            }
            else if (ambiguous)
            {
                matched.Add(node with { Note = "Several NKeeper hosts match it, so it wasn't added." });
            }
            else
            {
                var proposal = Propose(node, ip, hostname, scannedHostName);
                var namesake = named.Count > 0
                    ? $"“{named[0].Name}” has its name but a different {(HasOther(named[0].MacAddress, node.MacAddress) ? "MAC" : "IP")} address, so it's taken to be another host."
                    : null;
                var note = proposal is null ? "Not in NKeeper, and corosync has no address for it, so it can't be added from here." : namesake;
                matched.Add(node with { NewHost = proposal, Note = proposal is null && namesake is not null ? $"{note} {namesake}" : note });
            }
        }

        return matched;
    }

    /// <summary>Named like the node, at the address corosync reaches it on (its name serving as the hostname when it is one).</summary>
    private static ProposedHost? Propose(ScannedClusterNode node, string? ip, string? hostname, string scannedHostName)
    {
        hostname = hostname is not null && NetworkAddress.IsValidHostname(hostname) ? hostname
            : NetworkAddress.IsValidHostname(node.Name) ? node.Name
            : null;
        return node.Name.Length > 200 || (ip is null && node.Address is null)
            ? null
            : new ProposedHost(node.Name, hostname, ip, node.MacAddress, $"Added from a scan of {scannedHostName}: a node of its Proxmox cluster.");
    }

    private static bool HasOther(string? recorded, string? found) =>
        recorded is not null && found is not null && !recorded.Equals(found, StringComparison.OrdinalIgnoreCase);
}
