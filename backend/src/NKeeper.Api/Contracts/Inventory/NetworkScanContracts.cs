using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Contracts.Inventory;

/// <summary>
/// Starts a network scan: every address in the subnet is probed from the NKeeper server. The user
/// must confirm they know it is loud (<see cref="Acknowledged"/>).
/// </summary>
public sealed record NetworkScanRequest
{
    /// <summary>An IPv4 network in CIDR form, a /20 (4,096 addresses) at most, e.g. 192.168.1.0/24.</summary>
    [Required]
    [StringLength(64)]
    public string Subnet { get; init; } = string.Empty;

    /// <summary>That the user read the warning: the scan is visible on the network and may set off security tools.</summary>
    public bool Acknowledged { get; init; }
}

/// <summary>A network the NKeeper server is on, suggested as a scan target.</summary>
/// <param name="Subnet">The network, in CIDR form (a /24 around the server when its network is wider).</param>
/// <param name="Interface">The server's network interface on it.</param>
/// <param name="Address">The server's own address on it.</param>
public sealed record LocalNetworkResponse(string Subnet, string Interface, string Address);

/// <summary>
/// A machine a network scan found, with everything it learned about it, and the NKeeper host it
/// already is (matched by MAC address, then IP address, then hostname).
/// </summary>
/// <param name="IpAddress">Its address.</param>
/// <param name="MacAddress">From the server's neighbour (ARP) table, or the machine's NetBIOS answer.</param>
/// <param name="Vendor">The network card's maker, when its prefix is a known one (mostly virtual machines).</param>
/// <param name="IsVirtual">Whether the MAC address is a virtual machine's (or container's).</param>
/// <param name="Hostname">The best name found for it (reverse DNS, then NetBIOS, then its certificates).</param>
/// <param name="Names">Every name found, with where it came from, e.g. "server01 (NetBIOS)".</param>
/// <param name="OperatingSystem">A guess, from its SSH banner or its Windows services; null when nothing says.</param>
/// <param name="Ttl">The TTL of its ping reply, when it answered one.</param>
/// <param name="RespondedTo">How it was found: "ping", "tcp" and/or "arp".</param>
/// <param name="Ports">Its open TCP ports; those with a protocol can become NKeeper services.</param>
/// <param name="Details">What its services said: banners, web page titles, certificates, its workgroup.</param>
/// <param name="HostId">The NKeeper host it is, when one matched.</param>
/// <param name="MatchedHostName">That host's name.</param>
/// <param name="MatchedBy">"mac", "ip" or "hostname".</param>
/// <param name="NewHost">For a machine NKeeper doesn't have: the host that could be added for it.</param>
public sealed record DiscoveredDevice(
    string IpAddress,
    string? MacAddress,
    string? Vendor,
    bool IsVirtual,
    string? Hostname,
    IReadOnlyList<string> Names,
    string? OperatingSystem,
    int? Ttl,
    IReadOnlyList<string> RespondedTo,
    IReadOnlyList<ScannedPort> Ports,
    IReadOnlyList<string> Details,
    Guid? HostId,
    string? MatchedHostName,
    string? MatchedBy,
    ProposedHost? NewHost);

/// <summary>
/// A network scan's progress and what it has found so far. It runs on the server (it goes on if the
/// browser closes) and is only visible to the user who started it.
/// </summary>
/// <param name="State">"running", "finished", "cancelled" or "failed".</param>
/// <param name="Total">How many addresses it probes.</param>
/// <param name="Done">How many it has finished with.</param>
/// <param name="Devices">The machines that answered, in address order.</param>
/// <param name="Notes">What limits what it can see, e.g. no MAC addresses across a router.</param>
public sealed record NetworkScanResponse(
    string Id,
    string Subnet,
    string State,
    int Total,
    int Done,
    IReadOnlyList<DiscoveredDevice> Devices,
    IReadOnlyList<string> Notes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Message);
