using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NKeeper.Api.Controllers;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Inventory;

/// <summary>
/// An internal scan: which SSH service and login to use (a saved credential, or a one-off username
/// and password), and optionally an SSH host key the user chose to trust. The host comes from the URL.
/// </summary>
public sealed record InternalScanRequest : SessionStartMessage
{
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>Windows (RPC) scans: the domain for a one-off login.</summary>
    [StringLength(256)]
    public string? Domain { get; init; }
}

/// <summary>A listening (internal scan) or open (external scan) TCP port, with a well-known name when there is one.</summary>
public sealed record ScannedPort(int Port, string? Name, ServiceProtocol? Protocol);

/// <summary>A local account that can log in (a real shell), from the host's user database.</summary>
public sealed record ScannedUser(string Username, int Uid, string? FullName, string Shell);

/// <summary>
/// A virtual machine or container on the scanned hypervisor (libvirt), or anywhere in its Proxmox
/// cluster, and what NKeeper matched it to.
/// </summary>
/// <param name="Platform">"libvirt", "proxmox-qemu" or "proxmox-lxc".</param>
/// <param name="VirtualMachineId">Its identity on the platform: libvirt:&lt;uuid&gt; or proxmox:&lt;cluster&gt;/&lt;vmid&gt;.</param>
/// <param name="State">libvirt's state ("running", "shut off"…); null for Proxmox.</param>
/// <param name="Node">The Proxmox node it's on; null for libvirt (the scanned host).</param>
/// <param name="OnScannedHost">Whether it is on the scanned host itself (always for libvirt; for Proxmox, when its node is the scanned one).</param>
/// <param name="Addresses">IP addresses it was seen with (libvirt leases and ARP, Proxmox static configuration).</param>
/// <param name="GuestHostId">The NKeeper host it is, when one matched.</param>
/// <param name="MatchedBy">"id" (a stored VM identity), "mac" or "name".</param>
/// <param name="HypervisorHostId">The NKeeper host it runs on: the scanned host, or another node of the cluster.</param>
/// <param name="LinkProposed">Whether linking the guest to that host would change something.</param>
/// <param name="Note">Why it can't be linked, or what linking would change.</param>
/// <param name="NewHost">For a guest that isn't in NKeeper: the host that could be added for it, when there's an address to give it.</param>
/// <param name="CpuCores">The vCPUs the hypervisor gives it, when known.</param>
/// <param name="MemoryMiB">The memory the hypervisor gives it, in MiB, when known.</param>
/// <param name="Namesake">
/// When NKeeper has a host with its name that the scan took to be a different VM (its MAC address or
/// VM identity differs, or it runs elsewhere and nothing confirms it moved): why it wasn't matched.
/// </param>
public sealed record ScannedGuest(
    string Platform,
    string VirtualMachineId,
    string Name,
    string? State,
    string? Node,
    bool OnScannedHost,
    IReadOnlyList<string> MacAddresses,
    IReadOnlyList<string> Addresses,
    Guid? GuestHostId = null,
    string? GuestHostName = null,
    string? MatchedBy = null,
    Guid? HypervisorHostId = null,
    string? HypervisorHostName = null,
    bool LinkProposed = false,
    string? Note = null,
    ProposedHost? NewHost = null,
    int? CpuCores = null,
    int? MemoryMiB = null,
    string? Namesake = null);

/// <summary>
/// A host NKeeper has running on the scanned hypervisor (or another node of its Proxmox cluster) that
/// the scan didn't find there, nor anything like it: the VM was deleted or moved, so its link can be removed.
/// </summary>
/// <param name="HypervisorHostId">The host NKeeper has it running on.</param>
/// <param name="CanManage">Whether the user may unlink it.</param>
public sealed record MissingGuest(Guid HostId, string Name, Guid HypervisorHostId, string HypervisorHostName, bool CanManage);

/// <summary>A host a scan could add for a guest NKeeper doesn't have yet.</summary>
public sealed record ProposedHost(string Name, string? Hostname, string? IpAddress, string? MacAddress, string Description);

/// <summary>
/// A machine seen in the scanned host's neighbour (ARP/NDP) table: an IP address, the MAC it
/// resolved to, and what NKeeper matched it to. These are learned passively — the scan reads the
/// host's existing table and probes nothing — so they fill in the network without a loud scan.
/// </summary>
/// <param name="IpAddress">The neighbour's IP address.</param>
/// <param name="MacAddress">The MAC address it resolved to.</param>
/// <param name="HostId">The NKeeper host it already is, when one matched.</param>
/// <param name="MatchedBy">"mac" or "ip".</param>
/// <param name="NewHost">For a neighbour that isn't in NKeeper: the host that could be added for it.</param>
/// <param name="Note">Why it can't be added, when it can't.</param>
public sealed record ScannedNeighbor(
    string IpAddress,
    string? MacAddress,
    Guid? HostId = null,
    string? HostName = null,
    string? MatchedBy = null,
    ProposedHost? NewHost = null,
    string? Note = null);

/// <summary>
/// What a scan found. Nothing is saved: the client shows the findings and applies the ones the user
/// picks with an ordinary host update.
/// </summary>
/// <param name="IpAddress">The address the host was reached on.</param>
/// <param name="Ports">Every listening or open port found; those with a <see cref="ScannedPort.Protocol"/> can become services.</param>
/// <param name="Users">Internal scans only.</param>
/// <param name="Virtualization">Internal scans: what the host itself runs under (e.g. "kvm", "vmware", "lxc"), or null for physical hardware.</param>
/// <param name="Guests">Internal scans of hypervisors: the VMs and containers found there.</param>
/// <param name="Neighbors">Internal scans: machines in the host's neighbour (ARP/NDP) table, matched to NKeeper hosts.</param>
/// <param name="Notes">How values were found, and what couldn't be (e.g. no MAC address across a router).</param>
/// <param name="CpuCores">Internal scans: logical processors.</param>
/// <param name="MemoryMiB">Internal scans: memory in MiB.</param>
/// <param name="SerialNumber">Internal scans of physical hardware: the serial number (a Dell service tag).</param>
public sealed record ScanFindings(
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    string? OperatingSystem,
    IReadOnlyList<ScannedPort> Ports,
    bool IsKvmHost,
    IReadOnlyList<ScannedUser> Users,
    IReadOnlyList<string> Notes,
    string? Virtualization,
    IReadOnlyList<ScannedGuest> Guests,
    IReadOnlyList<ScannedNeighbor> Neighbors,
    int? CpuCores = null,
    int? MemoryMiB = null,
    string? SerialNumber = null)
{
    /// <summary>
    /// Internal scans of hypervisors: hosts linked to it (or its cluster's nodes) that the scan didn't
    /// find. Empty when the hypervisor's VM list couldn't be read in full.
    /// </summary>
    public IReadOnlyList<MissingGuest> MissingGuests { get; init; } = [];

    /// <summary>Whether some of the hypervisor's VMs couldn't be listed (so a VM not found may still be there).</summary>
    [JsonIgnore]
    public bool GuestListIncomplete { get; init; }
}

/// <param name="Status">"ready", or "trustRequired" when the SSH host key must be confirmed first (internal scans).</param>
public sealed record ScanResponse(string Status, ScanFindings? Findings, HostKeyPromptResponse? HostKey);
