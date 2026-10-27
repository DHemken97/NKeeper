using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>Changing many hosts at once: tags and the host VMs run on.</summary>
public sealed class BulkUpdateTests(PostgresServerFixture postgres) : IAsyncLifetime
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
    public async Task Tags_AreRemovedSetAndAdded_OnEverySelectedHost()
    {
        var web1 = await CreateAsync("web1", new { name = "env", value = "test" }, new { name = "role", value = "web" }, new { name = "legacy" });
        var web2 = await CreateAsync("web2", new { name = "ENV", value = "staging" }, new { name = "env", value = "test" });
        var other = await CreateAsync("db1", new { name = "env", value = "test" });

        var updated = await BulkAsync(new
        {
            hostIds = new[] { web1.Id, web2.Id },
            removeTags = new object[] { new { name = "LEGACY" } },
            setTags = new[] { new { name = "env", value = "production" } },
            addTags = new[] { new { name = "backup", value = "nightly" } },
        });

        Assert.Equal(["web1", "web2"], updated.Select(h => h.Name));
        Assert.Equal(["backup: nightly", "env: production", "role: web"], Tags(updated[0]));
        // Setting a tag replaces every value of that name, whatever its case.
        Assert.Equal(["backup: nightly", "env: production"], Tags(updated[1]));
        var untouched = (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{other.Id}", Json, Ct))!;
        Assert.Equal(["env: test"], Tags(untouched));

        // Removing one value leaves the others; a host the change doesn't affect isn't touched.
        var again = await BulkAsync(new { hostIds = new[] { web1.Id, web2.Id }, removeTags = new object[] { new { name = "role", value = "web" } } });
        Assert.Equal(["backup: nightly", "env: production"], Tags(again[0]));
        Assert.Equal(updated[1].UpdatedAtUtc, again[1].UpdatedAtUtc);
    }

    [Fact]
    public async Task TheHostVmsRunOn_IsSetAndCleared_WithoutLoops()
    {
        var node = await CreateAsync("node1");
        var vm1 = await CreateAsync("vm1");
        var vm2 = await CreateAsync("vm2");

        var linked = await BulkAsync(new { hostIds = new[] { vm1.Id, vm2.Id }, hypervisor = new { hypervisorId = node.Id } });
        Assert.All(linked, h => Assert.Equal((true, (Guid?)node.Id), (h.IsVirtualMachine, h.HypervisorId)));

        // The node can't run on one of its own VMs, nor on itself.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { hostIds = new[] { node.Id }, hypervisor = new { hypervisorId = vm1.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { hostIds = new[] { node.Id, vm1.Id }, hypervisor = new { hypervisorId = node.Id } })).StatusCode);

        var cleared = await BulkAsync(new { hostIds = new[] { vm1.Id }, hypervisor = new { hypervisorId = (Guid?)null } });
        Assert.Equal((true, (Guid?)null), (cleared[0].IsVirtualMachine, cleared[0].HypervisorId));
    }

    [Fact]
    public async Task Services_AreAddedAfterEachHostsOwn_UnlessItHasThemAlready()
    {
        var web1 = await CreateAsync("web1");
        var web2 = await CreateAsync("web2");
        var ssh = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{web2.Id}", new
        {
            name = "web2",
            hostname = "web2.example.com",
            services = new object[] { new { protocol = "Ssh", label = "Shell" }, new { protocol = "Https" } },
            version = web2.Version,
        }, Ct);
        ssh.EnsureSuccessStatusCode();
        web2 = (await ssh.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;

        var updated = await BulkAsync(new
        {
            hostIds = new[] { web1.Id, web2.Id },
            addServices = new object[] { new { protocol = "Ssh" }, new { protocol = "Rdp", port = 3390, rdpSecurity = "Tls" } },
        });

        Assert.Equal(["Ssh", "Rdp:3390"], Services(updated[0]));
        Assert.Equal(RdpSecurityMode.Tls, updated[0].Services[1].RdpSecurity);
        // web2's own SSH service keeps its label; the new one follows its services.
        Assert.Equal(["Ssh", "Https", "Rdp:3390"], Services(updated[1]));
        Assert.Equal("Shell", updated[1].Services[0].Label);

        // Nothing new: the hosts aren't touched.
        var again = await BulkAsync(new { hostIds = new[] { web1.Id, web2.Id }, addServices = new object[] { new { protocol = "Ssh" } } });
        Assert.Equal(updated.Select(h => h.UpdatedAtUtc), again.Select(h => h.UpdatedAtUtc));
    }

    [Fact]
    public async Task BulkServices_CantBeIdrac_OrPassTwentyPerHost()
    {
        var host = await CreateAsync("web1");
        var idrac = await PostAsync(new { hostIds = new[] { host.Id }, addServices = new object[] { new { protocol = "Idrac", address = "10.0.0.5" } } });
        Assert.Equal(HttpStatusCode.BadRequest, idrac.StatusCode);

        var services = Enumerable.Range(1, 20).Select(port => new { protocol = "Ssh", port = 2200 + port }).ToArray();
        Assert.Single(await BulkAsync(new { hostIds = new[] { host.Id }, addServices = services }));
        var tooMany = await PostAsync(new { hostIds = new[] { host.Id }, addServices = new object[] { new { protocol = "Rdp" } } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task BulkChanges_NeedAChange_AndManageAccessToEveryHost()
    {
        var host = await CreateAsync("web1");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { hostIds = new[] { host.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(new { hostIds = new[] { host.Id, Guid.NewGuid() }, addTags = new[] { new { name = "x" } } })).StatusCode);

        await _client.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        var denied = await bob.SendJsonAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { host.Id }, addTags = new[] { new { name = "x" } } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static List<string> Services(HostResponse host) => [.. host.Services.Select(s => s.Port is null ? $"{s.Protocol}" : $"{s.Protocol}:{s.Port}")];

    private static List<string> Tags(HostResponse host) => [.. host.Tags.Select(t => t.Value is null ? t.Name : $"{t.Name}: {t.Value}")];

    private Task<HttpResponseMessage> PostAsync(object body) => _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/bulk", body, Ct);

    private async Task<List<HostResponse>> BulkAsync(object body)
    {
        var response = await PostAsync(body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<HostResponse>>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateAsync(string name, params object[] tags)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, hostname = $"{name}.example.com", tags }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }
}
