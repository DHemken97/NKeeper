using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Hosts' CPU cores, memory and serial number: stored, found by scans, and given to VMs by their hypervisors.</summary>
public sealed class HostHardwareTests(PostgresServerFixture postgres) : IAsyncLifetime
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

    private const string ServerOutput = """
        @@hostname
        r750.example.com
        @@virt
        none
        @@cpus
        128
        @@memory
        online 549755813888
        memtotal 527999872
        bytes
        @@serial
        ABC1234
        @@end
        """;

    [Fact]
    public void LinuxScan_ReadsCpusInstalledMemoryAndSerial()
    {
        var findings = InternalScan.Parse(ServerOutput);

        // lsmem's installed memory (512 GiB), not the kernel's slightly smaller MemTotal.
        Assert.Equal((128, 524288, "ABC1234"), (findings.CpuCores, findings.MemoryMiB, findings.SerialNumber));
    }

    [Fact]
    public void LinuxScan_FallsBackToMemTotal_AndSkipsSerialsThatArentReal()
    {
        var withoutLsmem = InternalScan.Parse(ServerOutput.Replace("online 549755813888", "online", StringComparison.Ordinal));
        Assert.Equal(515624, withoutLsmem.MemoryMiB);

        // A VM's serial is made up by its hypervisor.
        Assert.Null(InternalScan.Parse(ServerOutput.Replace("@@virt\nnone", "@@virt\nkvm", StringComparison.Ordinal)).SerialNumber);

        var unreadable = InternalScan.Parse(ServerOutput.Replace("ABC1234", "(unreadable)", StringComparison.Ordinal));
        Assert.Null(unreadable.SerialNumber);
        Assert.Contains(unreadable.Notes, n => n.Contains("as root", StringComparison.Ordinal));

        Assert.Null(InternalScan.Serial("To Be Filled By O.E.M."));
        Assert.Null(InternalScan.Serial("0000000000"));
        Assert.Equal("CZ1234ABCD", InternalScan.Serial(" CZ1234ABCD "));
    }

    [Fact]
    public void HypervisorScan_ReadsWhatEachVmIsGiven()
    {
        const string output = """
            @@hostname
            pve1
            @@libvirtvms
            9f0c6b5e-1111-4a2b-9c3d-000000000001	web01	running	52:54:00:aa:bb:01,		4	8388608
            9f0c6b5e-1111-4a2b-9c3d-000000000002	db01	shut off
            @@proxmox
            cluster	lab
            node	pve1
            vm	qemu	pve1	101	app01	BC:24:11:00:00:01,		4	2	16384
            vm	qemu	pve1	102	tiny
            vm	lxc	pve1	200	dns01	BC:24:11:00:00:03,	10.0.0.53,		 	512
            @@end
            """;

        var guests = InternalScan.Parse(output).Guests;

        Assert.Equal(
            [("web01", 4, 8192), ("db01", null, null), ("app01", 8, 16384), ("tiny", 1, null), ("dns01", null, 512)],
            guests.Select(g => (g.Name, g.CpuCores, g.MemoryMiB)));
    }

    [Fact]
    public void WindowsScan_AddsUpMemoryModules_AndKeepsOnlyHardwareSerials()
    {
        Assert.Equal(65536, WmiMapping.MemoryMiB([34359738368, 34359738368], 68_600_000_000));
        // Without module details (some VMs), the total Windows sees.
        Assert.Equal(16383, WmiMapping.MemoryMiB([], 17_178_820_608));
        Assert.Equal("5CG1234XYZ", WmiMapping.Serial("5CG1234XYZ", virtualization: null));
        Assert.Null(WmiMapping.Serial("VMware-56 4d 2a", virtualization: "vmware"));
    }

    [Fact]
    public void LinkedVms_WhoseResourcesChanged_AreProposedForUpdate()
    {
        var node = new GuestMatcher.Candidate(Guid.NewGuid(), "node1", null, null, false, null, null, true);
        var vm = new GuestMatcher.Candidate(Guid.NewGuid(), "web01", null, null, true, node.Id, "libvirt:1", true, CpuCores: 2, MemoryMiB: 4096);
        ScannedGuest Guest(int? cpus, int? memory) => new("libvirt", "libvirt:1", "web01", "running", null, true, [], []) { CpuCores = cpus, MemoryMiB = memory };

        var unchanged = GuestMatcher.Match(node.Id, [Guest(2, 4096)], [node, vm]).Single();
        Assert.False(unchanged.LinkProposed);

        var resized = GuestMatcher.Match(node.Id, [Guest(4, 8192)], [node, vm]).Single();
        Assert.True(resized.LinkProposed);
        Assert.Equal("Updates its resources to 4 vCPUs, 8 GiB.", resized.Note);
    }

    [Fact]
    public async Task Hardware_IsSavedWithTheHost_AndValidated()
    {
        var created = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "r750",
            ipAddress = "10.0.0.20",
            cpuCores = 128,
            memoryMiB = 524288,
            serialNumber = "  ABC1234 ",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var host = (await created.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
        Assert.Equal((128, 524288, "ABC1234"), (host.CpuCores, host.MemoryMiB, host.SerialNumber));

        var invalid = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "bad", ipAddress = "10.0.0.21", cpuCores = 0, memoryMiB = -1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = await invalid.Content.ReadAsStringAsync(Ct);
        Assert.Contains("cpuCores", errors, StringComparison.Ordinal);
        Assert.Contains("memoryMiB", errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScansRecordWhatVmsAreGiven_WhenLinkingOrAddingThem()
    {
        var node = await CreateAsync("node1", "10.0.0.1");
        var vm = await CreateAsync("web01", "10.0.0.50");

        var linked = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/hypervisor-links", new
        {
            links = new[] { new { guestId = vm.Id, hypervisorId = node.Id, virtualMachineId = "libvirt:1", cpuCores = 4, memoryMiB = 8192 } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal((4, 8192), (await GetAsync(vm.Id)) is var updated ? (updated.CpuCores, updated.MemoryMiB) : default);

        var added = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/from-scan", new
        {
            hosts = new[] { new { name = "db01", ipAddress = "10.0.0.51", hypervisorId = node.Id, virtualMachineId = "libvirt:2", cpuCores = 8, memoryMiB = 32768 } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var db = Assert.Single((await added.Content.ReadFromJsonAsync<List<HostResponse>>(Json, Ct))!);
        Assert.Equal((8, 32768), (db.CpuCores, db.MemoryMiB));
    }

    private async Task<HostResponse> CreateAsync(string name, string ip)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = ip }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> GetAsync(Guid id) => (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{id}", Json, Ct))!;
}
