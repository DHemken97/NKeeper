using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Storage;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// A host's iSCSI sessions, multipath maps and DRBD resources over SSH, read from and changed with
/// (fake) iscsiadm, multipath, drbdadm and drbdsetup run with sudo, and audited.
/// </summary>
public sealed class StorageTests(PostgresServerFixture postgres, StorageServerFixture san) : IAsyncLifetime, IClassFixture<StorageServerFixture>
{
    private const string Disk1 = "iqn.2003-01.org.example.san:disk1";
    private const string Disk2 = "iqn.2003-01.org.example.san:disk2";
    private const string Disk3 = "iqn.2003-01.org.example.san:disk3";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        san.SkipIfUnavailable();
        await san.ResetAsync();
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        (_client, _) = await SignedInCookies.SignInAsync(_factory, Ct);
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
    public async Task Status_AsksToTrustTheHostKey_ThenDetectsIscsiMultipathAndDrbd()
    {
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/storage/status", Request(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var answer = await PostAsync("/api/storage/status", Request(host, trust: first.GetProperty("hostKey").GetProperty("fingerprint").GetString()));
        var storage = Storage(answer);

        var iscsi = storage.Iscsi!;
        Assert.Equal("iqn.1993-08.org.debian:01:san-test", iscsi.InitiatorName);
        var session = Assert.Single(iscsi.Sessions);
        Assert.Equal((Disk1, "10.0.0.5:3260", "LOGGED_IN", "sdb"), (session.Target, session.Portal, session.State, string.Join(',', session.Disks)));
        Assert.Equal([new IscsiNode(Disk1, "10.0.0.5:3260", true), new IscsiNode(Disk2, "10.0.0.5:3260", false)], iscsi.Nodes);

        // A map without an alias is named by its WWID.
        Assert.Equal(["36001405bbbbbbbbbbbbbbbbbbbbbbbbb", "mpatha"], storage.Multipath!.Select(m => m.Name));
        var mpatha = storage.Multipath![1];
        Assert.Equal(("36001405aaaaaaaaaaaaaaaaaaaaaaaaa", "dm-0", "LIO-ORG,disk01", "10G"), (mpatha.Wwid, mpatha.DmDevice, mpatha.Product, mpatha.Size));
        Assert.Equal(
            [new MultipathPath("3:0:0:0", "sdb", "active", "ready", "running"), new MultipathPath("4:0:0:0", "sdc", "failed", "faulty", "offline")],
            mpatha.Paths);

        // r1 is configured but down.
        Assert.Equal(["r0", "r1"], storage.Drbd!.Select(r => r.Name));
        var r0 = storage.Drbd![0];
        Assert.Equal((true, "Secondary"), (r0.Up, r0.Role));
        Assert.Equal([new DrbdVolume(0, 0, "UpToDate")], r0.Volumes);
        var peer = Assert.Single(r0.Peers);
        Assert.Equal(("node2", "Connected", "Secondary"), (peer.Name, peer.ConnectionState, peer.Role));
        Assert.Equal([new DrbdPeerVolume(0, "SyncSource", "Inconsistent", 42.5)], peer.Volumes);
        var r1 = storage.Drbd![1];
        Assert.Equal(("r1", false, (string?)null, 0, 0), (r1.Name, r1.Up, r1.Role, r1.Volumes.Count, r1.Peers.Count));
    }

    [Fact]
    public async Task Iscsi_DiscoversLogsInAndLogsOut_AndAuditsEachAction()
    {
        var host = await CreateTrustedHostAsync();

        var discovered = await ActAsync(host, StorageActions.IscsiDiscover, portal: "10.0.0.9");
        Assert.Equal("Found 1 target, 1 new.", discovered.GetProperty("message").GetString());
        Assert.Contains(new IscsiNode(Disk3, "10.0.0.9:3260", false), Storage(discovered).Iscsi!.Nodes);

        var login = await ActAsync(host, StorageActions.IscsiLogin, target: Disk3, portal: "10.0.0.9:3260,1");
        Assert.Equal($"Logged in to {Disk3} at 10.0.0.9:3260; its disks here: sdc.", login.GetProperty("message").GetString());
        var loggedIn = Storage(login);
        Assert.Equal([Disk1, Disk3], loggedIn.Iscsi!.Sessions.Select(s => s.Target));

        var loggedOut = Storage(await ActAsync(host, StorageActions.IscsiLogout, target: Disk1, portal: "10.0.0.5:3260"));
        Assert.Equal([Disk3], loggedOut.Iscsi!.Sessions.Select(s => s.Target));
        Assert.Contains(new IscsiNode(Disk1, "10.0.0.5:3260", false), loggedOut.Iscsi.Nodes);

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=storage", Ct);
        Assert.Equal(
            [$"iSCSI logout · {Disk1} · 10.0.0.5:3260", $"iSCSI login · {Disk3} · 10.0.0.9:3260", "iSCSI discovery · 10.0.0.9"],
            audit.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Iscsi_RefusesTargetsTheHostDoesntKnow_AndExplainsWhatTheToolSaid()
    {
        var host = await CreateTrustedHostAsync();

        var unknown = await ActResponseAsync(host, StorageActions.IscsiLogin, target: Disk3, portal: "10.0.0.9:3260");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Discover it first", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var already = await ActResponseAsync(host, StorageActions.IscsiLogin, target: Disk1, portal: "10.0.0.5:3260");
        Assert.Contains("already logged in", await already.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var refused = await ActResponseAsync(host, StorageActions.IscsiDiscover, portal: "10.0.0.66:3260");
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Equal(
            "iSCSI discovery · 10.0.0.66:3260 failed: iscsiadm: cannot make connection to 10.0.0.66:3260: Connection refused.",
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ALoginTheSanRedirects_IsKnownByThePortalItWasMadeThrough()
    {
        const string nimble = "iqn.2007-11.com.nimblestorage:vol1-v03854e0c5f768377.000004be.5cf3cd7b";
        var host = await CreateTrustedHostAsync();
        await ActAsync(host, StorageActions.IscsiDiscover, portal: "10.0.0.7");

        // The array connects the session to its data address, 10.0.1.7.
        var login = await ActAsync(host, StorageActions.IscsiLogin, target: nimble, portal: "10.0.0.7:3260,2460");
        Assert.Equal($"Logged in to {nimble} at 10.0.0.7:3260; its disks here: sdc.", login.GetProperty("message").GetString());
        var session = Storage(login).Iscsi!.Sessions.Single(s => s.Target == nimble);
        Assert.Equal(("10.0.0.7:3260", "10.0.1.7:3260"), (session.Portal, Assert.Single(session.ConnectedTo!)));
        Assert.Contains(new IscsiNode(nimble, "10.0.0.7:3260", true), Storage(login).Iscsi!.Nodes);

        // Migrations check sessions the same way.
        Assert.Equal(0, await san.ExecAsync("p=''; " + NKeeper.Api.Migration.MigrationJob.IscsiSession(nimble, "10.0.0.7:3260")));
        Assert.NotEqual(0, await san.ExecAsync("p=''; " + NKeeper.Api.Migration.MigrationJob.IscsiSession(nimble, "10.0.1.7:3260")));

        var loggedOut = Storage(await ActAsync(host, StorageActions.IscsiLogout, target: nimble, portal: "10.0.0.7:3260"));
        Assert.DoesNotContain(loggedOut.Iscsi!.Sessions, s => s.Target == nimble);
    }

    [Fact]
    public async Task ALoginThatExitsCleanlyButDoesntLogIn_IsReportedAsFailed()
    {
        var host = await CreateTrustedHostAsync();
        await san.MakeLoginsSilentAsync();

        var response = await ActResponseAsync(host, StorageActions.IscsiLogin, target: Disk2, portal: "10.0.0.5:3260");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            $"iSCSI login · {Disk2} · 10.0.0.5:3260 didn't take effect: the host has no session to {Disk2} at 10.0.0.5:3260 afterwards. The tool printed nothing and reported success.",
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("10.0.0.9; reboot")]
    [InlineData("10.0.0.9' && reboot '")]
    [InlineData("$(reboot)")]
    [InlineData("-p 10.0.0.9")]
    public async Task Discovery_RefusesPortalsThatArentAnAddress(string portal)
    {
        var host = await CreateTrustedHostAsync();

        var response = await ActResponseAsync(host, StorageActions.IscsiDiscover, portal: portal);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Enter the portal", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multipath_FlushesAMap_AndReportsAMapInUse()
    {
        var host = await CreateTrustedHostAsync();

        var flushed = Storage(await ActAsync(host, StorageActions.MultipathFlush, name: "mpatha"));
        Assert.Equal(["36001405bbbbbbbbbbbbbbbbbbbbbbbbb"], flushed.Multipath!.Select(m => m.Name));

        var inUse = await ActResponseAsync(host, StorageActions.MultipathFlush, name: "36001405bbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(HttpStatusCode.BadGateway, inUse.StatusCode);
        Assert.Contains("map in use", await inUse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var gone = await ActResponseAsync(host, StorageActions.MultipathFlush, name: "mpatha");
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);

        Assert.NotNull(Storage(await ActAsync(host, StorageActions.MultipathReload)).Multipath);
    }

    [Fact]
    public async Task Drbd_BringsResourcesUp_ChangesRoles_AndConnections()
    {
        var host = await CreateTrustedHostAsync();

        var up = Storage(await ActAsync(host, StorageActions.DrbdUp, name: "r1"));
        Assert.Equal((true, "Secondary"), (up.Drbd![1].Up, up.Drbd[1].Role));

        var primary = Storage(await ActAsync(host, StorageActions.DrbdPrimary, name: "r0"));
        Assert.Equal("Primary", primary.Drbd![0].Role);

        var disconnected = Storage(await ActAsync(host, StorageActions.DrbdDisconnect, name: "r0"));
        Assert.Equal("StandAlone", disconnected.Drbd![0].Peers[0].ConnectionState);

        var down = Storage(await ActAsync(host, StorageActions.DrbdDown, name: "r1"));
        Assert.False(down.Drbd![1].Up);

        var unknown = await ActResponseAsync(host, StorageActions.DrbdUp, name: "r9");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("no DRBD resource", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutRoot_ExplainsThatSudoIsNeeded()
    {
        var host = await CreateTrustedHostAsync();
        await san.RemoveSudoAsync();

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/storage/status", Request(host), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("sudo without a password", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownActions_AreRefused()
    {
        var host = await CreateTrustedHostAsync();

        var response = await ActResponseAsync(host, "drbd.invalidate", name: "r0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StorageStatus Storage(JsonElement answer) => answer.GetProperty("storage").Deserialize<StorageStatus>(Json)!;

    private static object Request(HostResponse host, string? trust = null) =>
        new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = trust };

    private async Task<JsonElement> ActAsync(HostResponse host, string action, string? target = null, string? portal = null, string? name = null)
    {
        var response = await ActResponseAsync(host, action, target, portal, name);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<HttpResponseMessage> ActResponseAsync(HostResponse host, string action, string? target = null, string? portal = null, string? name = null) =>
        _client.SendJsonAsync(
            HttpMethod.Post, "/api/storage/action",
            new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, action, target, portal, name }, Ct);

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, path, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HostResponse> CreateHostAsync()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"san-{Guid.NewGuid():N}",
            ipAddress = StorageServerFixture.Address,
            services = new[] { new { protocol = "Ssh", port = (int?)san.Port } },
            credentials = new[] { new { username = StorageServerFixture.UserName, password = StorageServerFixture.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateTrustedHostAsync()
    {
        var host = await CreateHostAsync();
        var first = await PostAsync("/api/storage/status", Request(host));
        await PostAsync("/api/storage/status", Request(host, trust: first.GetProperty("hostKey").GetProperty("fingerprint").GetString()));
        return host;
    }
}
