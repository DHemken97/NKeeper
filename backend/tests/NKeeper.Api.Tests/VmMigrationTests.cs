using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Migration;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Moving a libvirt VM between two (fake) KVM hosts with its storage: an image file on a file system
/// on DRBD, handed over from one host to the other, and put back when a step fails.
/// </summary>
public sealed class VmMigrationTests(PostgresServerFixture postgres, MigrationHostsFixture kvm) : IAsyncLifetime, IClassFixture<MigrationHostsFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private HostResponse _source = null!;
    private HostResponse _target = null!;
    private readonly Dictionary<string, string> _trust = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        kvm.SkipIfUnavailable();
        await kvm.ResetAsync();
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        (_client, _) = await SignedInCookies.SignInAsync(_factory, Ct);
        _source = await CreateHostAsync("kvm-a", MigrationHostsFixture.PortOf(kvm.Source));
        _target = await CreateHostAsync("kvm-b", MigrationHostsFixture.PortOf(kvm.Target));
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Plan_TracesTheDiskToDrbd_TestsSshBetweenTheHosts_AndPlansAnOfflineHandover()
    {
        var plan = await PlanAsync(new MigrationOptions { Live = true });

        var disk = Assert.Single(plan.Disks);
        Assert.Equal(("vda", DiskLayouts.Filesystem, "/srv/vms"), (disk.Name, disk.Layout, disk.Mount!.MountPoint));
        Assert.Equal([new StorageLayer(LayerKinds.Drbd, "r0", "0")], disk.Layers);
        Assert.Equal("offline", plan.Mode);
        Assert.Empty(plan.Blockers);
        Assert.Equal(
            ["drbd-ready", "guest-shutdown", "umount", "drbd-secondary", "drbd-primary", "mount", "check-paths", "guest-move", "guest-start"],
            plan.Steps.Select(s => s.Kind));
        Assert.Contains("file system that can't be mounted on two hosts at once", plan.Choices.LiveUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_HandsTheStorageOver_MovesTheVm_AndStartsItOnTheNewHost()
    {
        var job = await RunAsync(new MigrationOptions { Live = true, DrbdDownOnOld = true });

        Assert.True(job.State == "succeeded", Describe(job));
        Assert.Equal("Moved to kvm-b.", job.Message);
        Assert.Equal("running", await VirshAsync(kvm.Target, "domstate"));
        Assert.Contains("failed to get domain", await VirshAsync(kvm.Source, "domstate"), StringComparison.Ordinal);
        Assert.Equal(("", "/dev/drbd0 /srv/vms ext4 1111-2222"), (await MountsAsync(kvm.Source), await MountsAsync(kvm.Target)));
        Assert.Equal(("down", "Primary"), (await RoleAsync(kvm.Source), await RoleAsync(kvm.Target)));
        Assert.Equal("qcow2 image", (await MigrationHostsFixture.ExecAsync(kvm.Target, "cat /srv/vms/web.qcow2")).Trim());

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?q=kvm-b", Ct);
        Assert.Equal(
            ["migration.end", "migration.start"],
            audit.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("action").GetString()).Where(a => a!.StartsWith("migration.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WhenAStepFails_EverythingIsPutBack_AndTheVmRunsOnTheOldHostAgain()
    {
        await MigrationHostsFixture.ExecAsync(kvm.Target, "touch /var/lib/fake-mount/fail");

        var job = await RunAsync(new MigrationOptions());

        Assert.True(job.State == "rolledBack", Describe(job));
        var failed = Assert.Single(job.Steps, s => s.State == "failed");
        Assert.Equal("Mount /srv/vms (ext4) on kvm-b", failed.Title);
        Assert.Contains("wrong fs type", job.Message, StringComparison.Ordinal);
        Assert.Equal(["undone", "undone", "undone", "undone"], job.Steps.Where(s => s.State.StartsWith("undo", StringComparison.Ordinal)).Select(s => s.State));
        Assert.Equal("running", await VirshAsync(kvm.Source, "domstate"));
        Assert.Contains("failed to get domain", await VirshAsync(kvm.Target, "domstate"), StringComparison.Ordinal);
        Assert.Equal(("/dev/drbd0 /srv/vms ext4 1111-2222", ""), (await MountsAsync(kvm.Source), await MountsAsync(kvm.Target)));
        Assert.Equal(("Primary", "Secondary"), (await RoleAsync(kvm.Source), await RoleAsync(kvm.Target)));
    }

    [Fact]
    public async Task Start_RefusesWhatThePlanBlocks()
    {
        await PlanAsync(new MigrationOptions()); // Trusts both hosts.
        await MigrationHostsFixture.ExecAsync(kvm.Target, $"cp /dev/null /var/lib/fake-virsh/{MigrationHostsFixture.VmUuid}.xml && echo 'shut off' > /var/lib/fake-virsh/{MigrationHostsFixture.VmUuid}.state");

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/migrations", Request(new MigrationOptions()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("kvm-b already has a VM with this UUID defined", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_RunsOnlyTheStepsTheUserConfirmed()
    {
        var plan = await PlanAsync(new MigrationOptions());
        var titles = plan.Steps.Select(s => s.Title).ToArray();

        foreach (var confirmed in new[] { null, titles[..^1], [.. titles.Reverse()] })
        {
            var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/migrations", Request(new MigrationOptions(), confirmed), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Review the steps again", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal("running", await VirshAsync(kvm.Source, "domstate"));
    }

    private async Task<MigrationPlan> PlanAsync(MigrationOptions options)
    {
        var answer = await PostTrustingAsync("/api/migrations/plan", options);
        return answer.GetProperty("plan").Deserialize<MigrationPlan>(Json)!;
    }

    private async Task<MigrationJobView> RunAsync(MigrationOptions options)
    {
        var plan = await PlanAsync(options);
        var started = await PostTrustingAsync("/api/migrations", options, [.. plan.Steps.Select(s => s.Title)]);
        var id = started.GetProperty("jobId").GetString()!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (true)
        {
            var job = (await _client.GetFromJsonAsync<MigrationJobView>($"/api/migrations/{id}", Json, Ct))!;
            if (job.State != "running" || DateTimeOffset.UtcNow > deadline)
            {
                return job;
            }

            await Task.Delay(500, Ct);
        }
    }

    /// <summary>Posts, trusting each host key the answer asks about (one per login), until it's answered.</summary>
    private async Task<JsonElement> PostTrustingAsync(string path, MigrationOptions options, string[]? confirmedSteps = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await _client.SendJsonAsync(HttpMethod.Post, path, Request(options, confirmedSteps), Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var answer = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            if (answer.GetProperty("status").GetString() != "trustRequired")
            {
                return answer;
            }

            _trust[answer.GetProperty("login").GetString()!] = answer.GetProperty("hostKey").GetProperty("fingerprint").GetString()!;
        }

        throw new InvalidOperationException("Still asked to trust a host after trusting both.");
    }

    private object Request(MigrationOptions options, string[]? confirmedSteps = null) => new
    {
        via = "kvm",
        vmHostId = _source.Id,
        vm = MigrationHostsFixture.VmUuid,
        source = new { hostId = _source.Id, serviceIndex = 0, credentialId = _source.Credentials[0].Id, trustFingerprint = _trust.GetValueOrDefault("source") },
        target = new { hostId = _target.Id, serviceIndex = 0, credentialId = _target.Credentials[0].Id, trustFingerprint = _trust.GetValueOrDefault("target") },
        options,
        confirmedSteps,
    };

    private static string Describe(MigrationJobView job) =>
        $"{job.State}: {job.Message}\n{string.Join('\n', job.Steps.Select(s => $"  {s.State,-10} {s.Title}{(s.Message is null ? "" : $" — {s.Message}")}"))}";

    private static async Task<string> VirshAsync(DotNet.Testcontainers.Containers.IContainer host, string command)
    {
        var result = await host.ExecAsync(["virsh", command, MigrationHostsFixture.VmUuid]);
        return (result.Stdout + result.Stderr).Trim();
    }

    private static async Task<string> MountsAsync(DotNet.Testcontainers.Containers.IContainer host) =>
        (await MigrationHostsFixture.ExecAsync(host, "cat /var/lib/fake-mount/table")).Trim();

    private static async Task<string> RoleAsync(DotNet.Testcontainers.Containers.IContainer host) =>
        (await MigrationHostsFixture.ExecAsync(host, "cat /var/lib/fake-drbd/r0 2>/dev/null || echo down")).Trim();

    private async Task<HostResponse> CreateHostAsync(string name, int port)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = MigrationHostsFixture.Address,
            services = new[] { new { protocol = "Kvm", port = (int?)port } },
            credentials = new[] { new { username = MigrationHostsFixture.UserName, password = MigrationHostsFixture.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }
}
