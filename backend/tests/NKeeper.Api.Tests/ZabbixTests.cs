using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Zabbix: settings and their test, a linked host's overview, searching, comparing, linking and adding hosts from Zabbix.</summary>
public sealed class ZabbixTests(PostgresServerFixture postgres, ZabbixServerFixture zabbix) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Test_AsksToTrustTheCertificate_ThenReportsTheVersionAndHosts()
    {
        var first = await PostAsync("/api/admin/zabbix/test", new { url = zabbix.Url, apiToken = ZabbixServerFixture.ApiToken });
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        Assert.Equal(zabbix.CertificateFingerprint, first.GetProperty("certificate").GetProperty("fingerprint").GetString());

        var ready = await PostAsync("/api/admin/zabbix/test", new
        {
            url = $"{zabbix.Url}zabbix.php?action=dashboard.view",
            apiToken = ZabbixServerFixture.ApiToken,
            trustFingerprint = zabbix.CertificateFingerprint,
        });
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal(ZabbixServerFixture.Version, ready.GetProperty("version").GetString());
        Assert.Equal(3, ready.GetProperty("hostCount").GetInt32());
    }

    [Fact]
    public async Task Test_ReportsARefusedToken_OnTheTokenField()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/admin/zabbix/test", new
        {
            url = zabbix.Url, apiToken = "wrong", trustFingerprint = zabbix.CertificateFingerprint,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("apiToken", body, StringComparison.Ordinal);
        Assert.Contains("refused the API token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_KeepTheTokenSecret_AndTheTrustedCertificateForTheSameServer()
    {
        Assert.False((await GetAsync("/api/zabbix/status")).GetProperty("enabled").GetBoolean());

        var saved = await ConfigureAsync();
        Assert.True(saved.GetProperty("hasApiToken").GetBoolean());
        Assert.Equal(zabbix.Url, saved.GetProperty("url").GetString());
        Assert.Equal(zabbix.CertificateFingerprint, saved.GetProperty("trustedCertificateFingerprint").GetString());
        Assert.DoesNotContain(ZabbixServerFixture.ApiToken, saved.GetRawText(), StringComparison.Ordinal);

        // Saved again without a token or fingerprint: both are kept.
        var again = await PutAsync("/api/admin/zabbix", new { enabled = true, url = zabbix.Url });
        Assert.True(again.GetProperty("hasApiToken").GetBoolean());
        Assert.Equal(zabbix.CertificateFingerprint, again.GetProperty("trustedCertificateFingerprint").GetString());
        Assert.DoesNotContain(ZabbixServerFixture.ApiToken, (await GetAsync("/api/admin/zabbix")).GetRawText(), StringComparison.Ordinal);

        var status = await GetAsync("/api/zabbix/status");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(zabbix.Url, status.GetProperty("frontendUrl").GetString());

        // Another server: the certificate trusted for the old one doesn't carry over.
        var moved = await PutAsync("/api/admin/zabbix", new { enabled = true, url = "https://zabbix.example.com/" });
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("trustedCertificateFingerprint").ValueKind);
    }

    [Fact]
    public async Task Settings_RequireAnAddressAndToken_ToTurnOn()
    {
        var noToken = await _admin.SendJsonAsync(HttpMethod.Put, "/api/admin/zabbix", new { enabled = true, url = zabbix.Url }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Contains("apiToken", await noToken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var badUrl = await _admin.SendJsonAsync(HttpMethod.Put, "/api/admin/zabbix", new { enabled = true, url = "ftp://zabbix", apiToken = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
    }

    [Fact]
    public async Task Overview_ReadsTheLinkedHost()
    {
        await ConfigureAsync();
        var host = await CreateHostAsync("pve1", "192.168.42.2", zabbixHostId: "10084");
        Assert.Equal("10084", host.GetProperty("zabbixHostId").GetString());

        var overview = await GetAsync($"/api/hosts/{Id(host)}/zabbix");
        var summary = overview.GetProperty("host");
        Assert.Equal("10084", summary.GetProperty("hostId").GetString());
        Assert.True(summary.GetProperty("monitored").GetBoolean());
        Assert.Equal(["192.168.42.2"], Strings(summary.GetProperty("ipAddresses")));
        Assert.Equal(["AA:BB:CC:00:00:01"], Strings(summary.GetProperty("macAddresses")));
        Assert.Equal("Debian 12", summary.GetProperty("operatingSystem").GetString());
        Assert.Equal("Available", overview.GetProperty("availability").GetString());
        Assert.Equal("Proxmox node", overview.GetProperty("description").GetString());
        Assert.Equal(["Hypervisors"], Strings(overview.GetProperty("groups")));
        Assert.Equal("Rack 4", overview.GetProperty("inventory").GetProperty("location").GetString());
        Assert.False(overview.GetProperty("inventory").TryGetProperty("macaddress_b", out _));

        // Well-known items that have been collected, in a fixed order.
        var keys = overview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Equal(["system.uptime", "system.cpu.num", "vm.memory.size[total]"], keys);

        var problem = Assert.Single(overview.GetProperty("problems").EnumerateArray());
        Assert.Equal("High CPU utilization", problem.GetProperty("name").GetString());
        Assert.Equal(4, problem.GetProperty("severity").GetInt32());
    }

    [Fact]
    public async Task Overview_ExplainsWhyItCant()
    {
        var host = await CreateHostAsync("pve1", "192.168.42.2", zabbixHostId: "10084");
        var disabled = await _admin.GetAsync($"/api/hosts/{Id(host)}/zabbix", Ct);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Contains("zabbix_disabled", await disabled.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await ConfigureAsync();
        var unlinked = await CreateHostAsync("other", "10.1.1.1");
        var notLinked = await _admin.GetAsync($"/api/hosts/{Id(unlinked)}/zabbix", Ct);
        Assert.Equal(HttpStatusCode.NotFound, notLinked.StatusCode);
        Assert.Contains("zabbix_not_linked", await notLinked.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var gone = await CreateHostAsync("gone", "10.1.1.2", zabbixHostId: "99999");
        var missing = await _admin.GetAsync($"/api/hosts/{Id(gone)}/zabbix", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("zabbix_host_not_found", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostZabbixId_MustBeANumber()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "x", ipAddress = "10.0.0.1", zabbixHostId = "abc" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("zabbixHostId", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_FindsHostsByNameOrAddress()
    {
        await ConfigureAsync();

        var byName = await GetAsync("/api/zabbix/hosts?search=web");
        Assert.Equal(["10085"], byName.EnumerateArray().Select(h => h.GetProperty("hostId").GetString()));
        Assert.Equal(["web01.example.com"], Strings(byName[0].GetProperty("dnsNames")));

        var byAddress = await GetAsync("/api/zabbix/hosts?search=10.0.0.25");
        Assert.Equal(["10086"], byAddress.EnumerateArray().Select(h => h.GetProperty("hostId").GetString()));
        Assert.False(byAddress[0].GetProperty("monitored").GetBoolean());
    }

    [Fact]
    public async Task Search_IsForUsersWhoCanManageHosts()
    {
        await ConfigureAsync();
        await _factory.CreateUserAsync("bob", "bob@example.com", "Bob-password-123");
        var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-password-123", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/zabbix/hosts?search=web", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/zabbix/comparison", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/zabbix", Ct)).StatusCode);
    }

    [Fact]
    public async Task Comparison_LinesUpBothSides()
    {
        await ConfigureAsync();
        await CreateHostAsync("Proxmox 1", "192.168.42.2", zabbixHostId: "10084");
        await CreateHostAsync("web server", "10.0.0.20");
        await CreateHostAsync("lonely", "10.9.9.9");
        await CreateHostAsync("removed from zabbix", "10.9.9.10", zabbixHostId: "99999");

        var comparison = await GetAsync("/api/zabbix/comparison");

        var linked = Assert.Single(comparison.GetProperty("linked").EnumerateArray());
        Assert.Equal("Proxmox 1", linked.GetProperty("host").GetProperty("name").GetString());
        Assert.Equal("10084", linked.GetProperty("zabbix").GetProperty("hostId").GetString());

        var probable = Assert.Single(comparison.GetProperty("probableMatches").EnumerateArray());
        Assert.Equal("web server", probable.GetProperty("host").GetProperty("name").GetString());
        Assert.Equal("10085", probable.GetProperty("zabbix").GetProperty("hostId").GetString());
        Assert.Equal("ip", probable.GetProperty("matchedBy").GetString());

        Assert.Equal(["10086"], comparison.GetProperty("zabbixOnly").EnumerateArray().Select(h => h.GetProperty("hostId").GetString()));
        Assert.Equal(["lonely"], comparison.GetProperty("nKeeperOnly").EnumerateArray().Select(h => h.GetProperty("name").GetString()));
        Assert.Equal(["removed from zabbix"], comparison.GetProperty("brokenLinks").EnumerateArray().Select(h => h.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Links_SetAndClearZabbixHostIds()
    {
        var host = await CreateHostAsync("web server", "10.0.0.20");

        var linked = await PostAsync("/api/zabbix/links", new { links = new[] { new { hostId = Id(host), zabbixHostId = "10085" } } });
        Assert.Equal("10085", linked[0].GetProperty("zabbixHostId").GetString());

        var unlinked = await PostAsync("/api/zabbix/links", new { links = new[] { new { hostId = Id(host), zabbixHostId = (string?)null } } });
        Assert.Equal(JsonValueKind.Null, unlinked[0].GetProperty("zabbixHostId").ValueKind);
    }

    [Fact]
    public async Task Import_AddsHostsFromZabbixData_AndSkipsOnesAlreadyInNKeeper()
    {
        await ConfigureAsync();
        await CreateHostAsync("Already here", "10.0.0.250", zabbixHostId: "10086");

        var result = await PostAsync("/api/zabbix/import", new { zabbixHostIds = new[] { "10084", "10085", "10086", "424242" }, groupsAsTags = true });

        var created = result.GetProperty("created").EnumerateArray().ToDictionary(h => h.GetProperty("zabbixHostId").GetString()!);
        Assert.Equal(["10084", "10085"], created.Keys.Order());

        var pve = created["10084"];
        Assert.Equal("pve1", pve.GetProperty("name").GetString());
        Assert.Equal("192.168.42.2", pve.GetProperty("ipAddress").GetString());
        Assert.Equal("AA:BB:CC:00:00:01", pve.GetProperty("macAddress").GetString());
        Assert.Equal("SN-PVE1", pve.GetProperty("serialNumber").GetString());
        Assert.Equal("Proxmox node", pve.GetProperty("description").GetString());
        var tags = pve.GetProperty("tags").EnumerateArray().Select(t => $"{t.GetProperty("name").GetString()}={t.GetProperty("value").GetString()}").ToList();
        // A tag name with a colon can't be searched for, so it's left out.
        Assert.Equal(["group=Hypervisors", "role=hypervisor"], tags);

        var web = created["10085"];
        Assert.Equal("Web 01", web.GetProperty("name").GetString());
        Assert.Equal("web01.example.com", web.GetProperty("hostname").GetString());
        Assert.Equal("10.0.0.20", web.GetProperty("ipAddress").GetString());

        var skipped = result.GetProperty("skipped").EnumerateArray().ToDictionary(s => s.GetProperty("zabbixHostId").GetString()!, s => s.GetProperty("reason").GetString());
        Assert.Contains("Already here", skipped["10086"], StringComparison.Ordinal);
        Assert.Contains("no host with this ID", skipped["424242"], StringComparison.Ordinal);
    }

    private async Task<JsonElement> ConfigureAsync() =>
        await PutAsync("/api/admin/zabbix", new
        {
            enabled = true,
            url = zabbix.Url,
            apiToken = ZabbixServerFixture.ApiToken,
            trustFingerprint = zabbix.CertificateFingerprint,
        });

    private async Task<JsonElement> CreateHostAsync(string name, string ip, string? zabbixHostId = null)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = ip, zabbixHostId }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string Id(JsonElement host) => host.GetProperty("id").GetString()!;

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    private async Task<JsonElement> GetAsync(string url)
    {
        var response = await _admin.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<JsonElement> PostAsync(string url, object body) => SendAsync(HttpMethod.Post, url, body);

    private Task<JsonElement> PutAsync(string url, object body) => SendAsync(HttpMethod.Put, url, body);

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, object body)
    {
        var response = await _admin.SendJsonAsync(method, url, body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
