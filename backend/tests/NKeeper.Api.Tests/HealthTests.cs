using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.Health;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Service health checks and Wake-on-LAN.</summary>
public sealed class HealthTests(PostgresServerFixture postgres, WebTargetFixture web) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), settings: new Dictionary<string, string> { ["Health:Timeout"] = "00:00:02" });
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Services_AreCheckedFromTheServer_WithTlsCertificates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = await CreateHostAsync("web01", new { protocol = "Https", port = web.HttpsPort }, new { protocol = "Ssh", port = openPort }, new { protocol = "Telnet", port = ClosedPort() });

        var checks = await CheckAsync(host.Id);

        var https = checks.Single(c => c.GetProperty("protocol").GetString() == "Https");
        Assert.True(https.GetProperty("isUp").GetBoolean());
        Assert.Equal("CN=nkeeper-web-target", https.GetProperty("certificateSubject").GetString());
        Assert.True(https.GetProperty("certificateExpiresUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(checks.Single(c => c.GetProperty("protocol").GetString() == "Ssh").GetProperty("isUp").GetBoolean());
        var telnet = checks.Single(c => c.GetProperty("protocol").GetString() == "Telnet");
        Assert.False(telnet.GetProperty("isUp").GetBoolean());
        // Refused at once on Linux; Windows retries a closed port for a while, so it may time out instead.
        Assert.False(string.IsNullOrEmpty(telnet.GetProperty("error").GetString()));

        // Everyone who can see the host sees its health.
        var all = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/health", Ct);
        Assert.Equal(3, all!.Count(c => c.GetProperty("hostId").GetGuid() == host.Id));
    }

    [Fact]
    public async Task ADownService_KeepsWhenItWentDown_UntilItsBackUp()
    {
        var port = ClosedPort();
        var host = await CreateHostAsync("flaky", new { protocol = "Ssh", port });

        var first = (await CheckAsync(host.Id)).Single();
        var downSince = first.GetProperty("downSinceUtc").GetDateTimeOffset();
        await Task.Delay(50, Ct);
        var second = (await CheckAsync(host.Id)).Single();
        Assert.Equal(downSince, second.GetProperty("downSinceUtc").GetDateTimeOffset());

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var up = (await CheckAsync(host.Id)).Single();
        Assert.True(up.GetProperty("isUp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, up.GetProperty("downSinceUtc").ValueKind);
    }

    [Fact]
    public async Task RemovedServices_LoseTheirChecks_AndOthersOnlySeeHostsTheyCanUse()
    {
        var host = await CreateHostAsync("two", new { protocol = "Ssh", port = ClosedPort() }, new { protocol = "Telnet", port = ClosedPort() });
        await CheckAsync(host.Id);
        var stored = await _admin.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct);
        await _admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = "two",
            ipAddress = "127.0.0.1",
            services = new[] { new { protocol = "Ssh", port = stored!.Services[0].Port } },
            version = stored.Version,
        }, Ct);

        Assert.Single(await CheckAsync(host.Id));

        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        Assert.Empty((await bob.GetFromJsonAsync<List<JsonElement>>("/api/health", Ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendJsonAsync(HttpMethod.Post, $"/api/health/hosts/{host.Id}/check", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task WakeOnLan_SendsTheMagicPacket_AndIsAudited()
    {
        var packet = WakeOnLan.MagicPacket("AA:BB:CC:DD:EE:0F");
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x0F }, packet[^6..]);

        var withMac = await CreateHostAsync("sleepy", mac: "aa-bb-cc-dd-ee-0f");
        var woken = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{withMac.Id}/wake", null, Ct);
        Assert.Equal(HttpStatusCode.OK, woken.StatusCode);
        Assert.Contains("255.255.255.255", (await woken.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sentTo").EnumerateArray().Select(a => a.GetString()));

        var withoutMac = await CreateHostAsync("nomac");
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{withoutMac.Id}/wake", null, Ct)).StatusCode);

        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?q=wake", Ct);
        Assert.Equal("sleepy", Assert.Single(audit.GetProperty("events").EnumerateArray()).GetProperty("hostName").GetString());
    }

    private async Task<HostResponse> CreateHostAsync(string name, params object[] services) => await CreateHostAsync(name, null, services);

    private async Task<HostResponse> CreateHostAsync(string name, string? mac, params object[] services)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = "127.0.0.1", macAddress = mac, services }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<List<JsonElement>> CheckAsync(Guid hostId)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/health/hosts/{hostId}/check", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>(Ct))!;
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
