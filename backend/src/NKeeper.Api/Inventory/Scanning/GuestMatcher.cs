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
        bool CanManage,
        int? CpuCores = null,
        int? MemoryMiB = null,
        string? IpAddress = null);

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

            if (host is null && ambiguous)
            {
                matched.Add(result with { Note = "Several NKeeper hosts match it, so it wasn't linked." });
            }
            else if (host is null)
            {
                var proposal = node is null ? null : Propose(guest, node);
                matched.Add(result with
                {
                    NewHost = proposal,
                    Note = proposal is not null ? "Not in NKeeper yet."
                        : node is null ? $"Not in NKeeper, and its node “{guest.Node}” isn't either."
                        : "Not in NKeeper, and the scan found no address for it (no IP address, and its name isn't a hostname), so it can't be added from here.",
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
                var linkChanges = !host.IsVirtualMachine || host.HypervisorId != node.Id || host.VirtualMachineId != guest.VirtualMachineId;
                var resourcesChange = (guest.CpuCores is { } cpus && cpus != host.CpuCores) || (guest.MemoryMiB is { } memory && memory != host.MemoryMiB);
                matched.Add(result with
                {
                    LinkProposed = linkChanges || resourcesChange,
                    Note = !linkChanges && !resourcesChange ? "Already linked."
                        : host.HypervisorId is { } previous && previous != node.Id ? $"Moved from {NameOf(byId, previous)}."
                        : host.HypervisorId is null ? "Not linked yet."
                        : linkChanges ? "Records its VM identity."
                        : $"Updates its resources to {Resources(guest)}.",
                });
            }
        }

        return matched;
    }

    /// <summary>
    /// The host to add for a guest NKeeper doesn't have: its name, an IP address it was seen with
    /// (or its name, when that's a valid hostname), and its first MAC address. Null without an address.
    /// </summary>
    private static ProposedHost? Propose(ScannedGuest guest, Candidate node)
    {
        var name = guest.Name.Trim();
        var ip = guest.Addresses.Count > 0 ? guest.Addresses[0] : null;
        var hostname = NetworkAddress.IsValidHostname(name) ? name : null;
        if (name.Length is 0 or > 200 || (ip is null && hostname is null))
        {
            return null;
        }

        var kind = guest.Platform switch { "proxmox-lxc" => "Proxmox container", "proxmox-qemu" => "Proxmox VM", _ => "libvirt VM" };
        return new ProposedHost(name, hostname, ip, guest.MacAddresses.Count > 0 ? guest.MacAddresses[0] : null, $"Added from a scan of {node.Name} ({kind} {guest.VirtualMachineId}).");
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

    /// <summary>e.g. "4 vCPUs, 8 GiB".</summary>
    internal static string Resources(ScannedGuest guest) => string.Join(", ", new[]
    {
        guest.CpuCores is { } cpus ? $"{cpus} vCPU{(cpus == 1 ? "" : "s")}" : null,
        guest.MemoryMiB is { } memory ? (memory % 1024 == 0 ? $"{memory / 1024} GiB" : $"{memory} MiB") : null,
    }.OfType<string>());

    private static string NameOf(Dictionary<Guid, Candidate> hosts, Guid id) => hosts.TryGetValue(id, out var host) ? $"“{host.Name}”" : "another host";
}
