using NKeeper.Api.Contracts.Inventory;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// Matches the VMs and containers found on a hypervisor to NKeeper hosts, and works out which
/// "runs on" links a scan should propose.
/// </summary>
internal static class GuestMatcher
{
    /// <summary>What matching needs to know about a host the user can see.</summary>
    internal sealed record Candidate(
        Guid Id,
        string Name,
        string? Hostname,
        string? MacAddress,
        bool IsVirtualMachine,
        Guid? HypervisorId,
        string? VirtualMachineId,
        bool CanManage);

    /// <summary>
    /// A guest is the host with its stored VM identity, else the one with one of its MAC addresses,
    /// else the one with its name (or hostname's first label); each only when exactly one host fits.
    /// Its node is the scanned host, or for Proxmox, the host named after the node.
    /// </summary>
    public static List<ScannedGuest> Match(Guid scannedHostId, IReadOnlyList<ScannedGuest> guests, IReadOnlyList<Candidate> hosts)
    {
        var byId = hosts.ToDictionary(h => h.Id);
        var matched = new List<ScannedGuest>(guests.Count);
        foreach (var guest in guests)
        {
            var node = guest.OnScannedHost ? byId.GetValueOrDefault(scannedHostId) : Single(hosts.Where(h => HasName(h, guest.Node)));
            var others = hosts.Where(h => h.Id != scannedHostId && h.Id != node?.Id).ToList();

            var (host, matchedBy, ambiguous) = Find(others, guest);
            var result = guest with
            {
                GuestHostId = host?.Id,
                GuestHostName = host?.Name,
                MatchedBy = matchedBy,
                HypervisorHostId = node?.Id,
                HypervisorHostName = node?.Name,
            };

            if (host is null)
            {
                matched.Add(result with
                {
                    Note = ambiguous ? "Several NKeeper hosts match it, so it wasn't linked." : "Not in NKeeper: no host has its name, MAC address or VM identity.",
                });
            }
            else if (!host.CanManage)
            {
                matched.Add(result with { Note = $"You can't change “{host.Name}”." });
            }
            else if (node is null)
            {
                matched.Add(result with { Note = $"Its node “{guest.Node}” isn't in NKeeper. Add it (named {guest.Node}) to link its VMs." });
            }
            else if (guest.Platform == "libvirt" && guest.State != "running" && host.HypervisorId is { } current && current != node.Id)
            {
                // With shared storage a VM is defined on several nodes but runs on one: don't move it.
                matched.Add(result with { Note = $"Defined here but {guest.State ?? "not running"}; it stays linked to {NameOf(byId, current)}." });
            }
            else
            {
                var changes = !host.IsVirtualMachine || host.HypervisorId != node.Id || host.VirtualMachineId != guest.VirtualMachineId;
                matched.Add(result with
                {
                    LinkProposed = changes,
                    Note = !changes ? "Already linked."
                        : host.HypervisorId is { } previous && previous != node.Id ? $"Moved from {NameOf(byId, previous)}."
                        : host.HypervisorId is null ? "Not linked yet."
                        : "Records its VM identity.",
                });
            }
        }

        return matched;
    }

    private static (Candidate? Host, string? MatchedBy, bool Ambiguous) Find(List<Candidate> hosts, ScannedGuest guest)
    {
        var ambiguous = false;
        foreach (var (by, matches) in new (string, IEnumerable<Candidate>)[]
        {
            ("id", hosts.Where(h => string.Equals(h.VirtualMachineId, guest.VirtualMachineId, StringComparison.OrdinalIgnoreCase))),
            ("mac", hosts.Where(h => h.MacAddress is { } mac && guest.MacAddresses.Contains(mac, StringComparer.OrdinalIgnoreCase))),
            ("name", hosts.Where(h => HasName(h, guest.Name))),
        })
        {
            var list = matches.Take(2).ToList();
            if (list.Count == 1)
            {
                return (list[0], by, false);
            }

            ambiguous |= list.Count > 1;
        }

        return (null, null, ambiguous);
    }

    private static Candidate? Single(IEnumerable<Candidate> hosts)
    {
        var list = hosts.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }

    /// <summary>The host's name or hostname, or either one's first label (VM names are usually short).</summary>
    private static bool HasName(Candidate host, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        static string FirstLabel(string value) => value.Split('.')[0];
        return new[] { host.Name, host.Hostname }
            .OfType<string>()
            .Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || FirstLabel(n).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameOf(Dictionary<Guid, Candidate> hosts, Guid id) => hosts.TryGetValue(id, out var host) ? $"“{host.Name}”" : "another host";
}
