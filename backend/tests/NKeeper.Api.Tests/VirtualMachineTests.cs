using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Virtual machines and the hosts they run on: the host fields, linking from scans, reading libvirt
/// and Proxmox inventories, and matching their guests to hosts.
/// </summary>
public sealed class VirtualMachineTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _client = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AVirtualMachine_RunsOnAnotherHost_WithoutLoops()
    {
        var node = await CreateAsync("node1", "10.0.0.1");
        var vm = await CreateAsync("app01", "10.0.0.50", isVirtualMachine: true, hypervisorId: node.Id);

        Assert.Equal((true, (Guid?)node.Id, (DateTimeOffset?)null), (vm.IsVirtualMachine, vm.HypervisorId, vm.HypervisorSeenAtUtc));

        // A VM can't run on itself, nor on a host that (eventually) runs on it.
        Assert.Equal(HttpStatusCode.BadRequest, (await UpdateAsync(vm, isVirtualMachine: true, hypervisorId: vm.Id)).StatusCode);
        var loop = await UpdateAsync(node, isVirtualMachine: true, hypervisorId: vm.Id);
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
        Assert.Contains("runs on this one", await loop.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A host that isn't a VM has no hypervisor.
        var physical = await (await UpdateAsync(vm, isVirtualMachine: false, hypervisorId: node.Id)).Content.ReadFromJsonAsync<HostResponse>(Json, Ct);
        Assert.Equal((false, (Guid?)null), (physical!.IsVirtualMachine, physical.HypervisorId));
    }

    [Fact]
    public async Task DeletingTheHypervisor_KeepsItsVms_AndClearsTheLink()
    {
        var node = await CreateAsync("node1", "10.0.0.1");
        var vm = await CreateAsync("app01", "10.0.0.50", isVirtualMachine: true, hypervisorId: node.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{node.Id}", null, Ct)).StatusCode);

        var after = await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{vm.Id}", Json, Ct);
        Assert.Equal((true, (Guid?)null), (after!.IsVirtualMachine, after.HypervisorId));
    }

    [Fact]
    public async Task ScanLinks_RecordWhereVmsRun_AndWhen()
    {
        var node1 = await CreateAsync("node1", "10.0.0.1");
        var node2 = await CreateAsync("node2", "10.0.0.2");
        var vm = await CreateAsync("app01", "10.0.0.50", isVirtualMachine: true, hypervisorId: node1.Id);

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/hypervisor-links", new
        {
            links = new[] { new { guestId = vm.Id, hypervisorId = node2.Id, virtualMachineId = "proxmox:lab/101" } },
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = Assert.Single((await response.Content.ReadFromJsonAsync<List<HostResponse>>(Json, Ct))!);
        Assert.Equal(((Guid?)node2.Id, "proxmox:lab/101"), (moved.HypervisorId, moved.VirtualMachineId));
        Assert.NotNull(moved.HypervisorSeenAtUtc);

        // Choosing the node by hand afterwards keeps the identity but drops the "seen by a scan" time.
        var manual = await (await UpdateAsync(moved, isVirtualMachine: true, hypervisorId: node1.Id)).Content.ReadFromJsonAsync<HostResponse>(Json, Ct);
        Assert.Equal(("proxmox:lab/101", (DateTimeOffset?)null), (manual!.VirtualMachineId, manual.HypervisorSeenAtUtc));
    }

    [Fact]
    public void LibvirtAndProxmoxInventories_AreParsed()
    {
        const string output = """
            @@hostname
            pve1
            @@virt
            none
            @@libvirtvms
            9f0c6b5e-1111-4a2b-9c3d-000000000001	web01	running	52:54:00:aa:bb:01,
            9f0c6b5e-1111-4a2b-9c3d-000000000002	db01	shut off
            @@proxmox
            cluster	lab
            node	pve1
            vm	qemu	pve1	101	app01	BC:24:11:00:00:01,BC:24:11:00:00:02,
            vm	lxc	pve2	200	dns01	bc:24:11:00:00:03,
            vm	qemu	pve2	102
            unreadable
            @@end
            """;

        var findings = InternalScan.Parse(output);

        Assert.Null(findings.Virtualization);
        Assert.Collection(
            findings.Guests,
            g => Assert.Equal(("libvirt", "libvirt:9f0c6b5e-1111-4a2b-9c3d-000000000001", "web01", "running", true, "52:54:00:AA:BB:01"), (g.Platform, g.VirtualMachineId, g.Name, g.State, g.OnScannedHost, g.MacAddresses.Single())),
            g => Assert.Equal(("db01", "shut off", 0), (g.Name, g.State, g.MacAddresses.Count)),
            g => Assert.Equal(("proxmox-qemu", "proxmox:lab/101", "app01", "pve1", true, 2), (g.Platform, g.VirtualMachineId, g.Name, g.Node, g.OnScannedHost, g.MacAddresses.Count)),
            g => Assert.Equal(("proxmox-lxc", "proxmox:lab/200", "dns01", "pve2", false, "BC:24:11:00:00:03"), (g.Platform, g.VirtualMachineId, g.Name, g.Node, g.OnScannedHost, g.MacAddresses.Single())),
            g => Assert.Equal(("VM 102", "pve2"), (g.Name, g.Node)));
        Assert.Contains(findings.Notes, n => n.Contains("as root", StringComparison.Ordinal));

        Assert.Equal("kvm", InternalScan.Parse(output.Replace("@@virt\nnone", "@@virt\nkvm", StringComparison.Ordinal)).Virtualization);
    }

    [Fact]
    public void Guests_MatchByIdentityMacOrName_AndOnlyRunningLibvirtVmsMoveLinks()
    {
        var pve1 = Host("pve1");
        var pve2 = Host("pve2", hostname: "pve2.lab.example");
        var app = Host("app01", isVm: true, hypervisor: pve2.Id, vmId: "proxmox:lab/101");
        var dns = Host("resolver", mac: "BC:24:11:00:00:03");
        var web = Host("web01.lab.example", isVm: true, hypervisor: pve2.Id);
        var db = Host("db01", isVm: true, hypervisor: pve2.Id);
        var locked = Host("ro01", canManage: false);
        GuestMatcher.Candidate[] hosts = [pve1, pve2, app, dns, web, db, locked];

        var matched = GuestMatcher.Match(pve1.Id, [
            Guest("proxmox-qemu", "proxmox:lab/101", "renamed", "pve1", true),
            Guest("proxmox-lxc", "proxmox:lab/200", "dns", "pve2", false, "BC:24:11:00:00:03"),
            Guest("libvirt", "libvirt:1", "web01", null, true, state: "running"),
            Guest("libvirt", "libvirt:2", "db01", null, true, state: "shut off"),
            Guest("proxmox-qemu", "proxmox:lab/300", "ro01", "pve1", true),
            Guest("proxmox-qemu", "proxmox:lab/400", "ghost", "pve1", true),
            Guest("proxmox-qemu", "proxmox:lab/500", "app01", "pve9", false),
        ], hosts);

        Assert.Equal((app.Id, "id", pve1.Id, true), (matched[0].GuestHostId, matched[0].MatchedBy, matched[0].HypervisorHostId, matched[0].LinkProposed));
        Assert.Contains("Moved from “pve2”", matched[0].Note, StringComparison.Ordinal);
        // Another node of the cluster, found by its hostname's first label.
        Assert.Equal((dns.Id, "mac", pve2.Id, true), (matched[1].GuestHostId, matched[1].MatchedBy, matched[1].HypervisorHostId, matched[1].LinkProposed));
        Assert.Equal((web.Id, "name", true), (matched[2].GuestHostId, matched[2].MatchedBy, matched[2].LinkProposed));
        Assert.False(matched[3].LinkProposed);
        Assert.Contains("stays linked to “pve2”", matched[3].Note, StringComparison.Ordinal);
        Assert.Equal((locked.Id, false), (matched[4].GuestHostId, matched[4].LinkProposed));
        Assert.Equal(((Guid?)null, false), (matched[5].GuestHostId, matched[5].LinkProposed));
        Assert.Contains("“pve9” isn't in NKeeper", matched[6].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalScanOfAKvmHost_ListsItsVms_MatchedToHosts()
    {
        ssh.SkipIfUnavailable();
        var node = await CreateAsync("kvm-node", SshServerFixture.Address, sshPort: ssh.Port);
        var guest = await CreateAsync("web vm", "10.0.0.80");
        var trust = await ScanAsync(node);
        var scan = await ScanAsync(node, trust.HostKey!.Fingerprint);

        var guests = scan.Findings!.Guests;
        // The fake virsh on the test server defines three domains.
        Assert.Equal(["db-vm", "vnc-vm", "web vm"], guests.Select(g => g.Name).Order(StringComparer.Ordinal));
        var web = Assert.Single(guests, g => g.Name == "web vm");
        Assert.Equal(((Guid?)guest.Id, "name", (Guid?)node.Id, true, "running"), (web.GuestHostId, web.MatchedBy, web.HypervisorHostId, web.LinkProposed, web.State));
        Assert.Equal("libvirt:11111111-1111-1111-1111-111111111111", web.VirtualMachineId);
    }

    private async Task<ScanResponse> ScanAsync(HostResponse host, string? trust = null)
    {
        var response = await _client.SendJsonAsync(
            HttpMethod.Post, $"/api/hosts/{host.Id}/scan/internal", new { serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = trust }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScanResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateAsync(string name, string ip, bool isVirtualMachine = false, Guid? hypervisorId = null, int? sshPort = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = ip,
            isVirtualMachine,
            hypervisorId,
            services = new[] { new { protocol = "Ssh", port = sshPort } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private Task<HttpResponseMessage> UpdateAsync(HostResponse host, bool isVirtualMachine, Guid? hypervisorId) =>
        _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = host.Name,
            ipAddress = host.IpAddress,
            isVirtualMachine,
            hypervisorId,
            services = host.Services.Select(s => new { protocol = s.Protocol.ToString(), port = s.Port }),
            credentials = host.Credentials.Select(c => new { id = c.Id, username = c.Username }),
            version = host.Version,
        }, Ct);

    private static GuestMatcher.Candidate Host(
        string name, string? hostname = null, string? mac = null, bool isVm = false, Guid? hypervisor = null, string? vmId = null, bool canManage = true) =>
        new(Guid.NewGuid(), name, hostname, mac, isVm, hypervisor, vmId, canManage);

    private static ScannedGuest Guest(string platform, string id, string name, string? node, bool onScannedHost, string? mac = null, string? state = null) =>
        new(platform, id, name, state, node, onScannedHost, mac is null ? [] : [mac]);
}
