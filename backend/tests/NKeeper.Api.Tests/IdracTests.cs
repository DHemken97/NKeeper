using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Sdk.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Ssh;
using NKeeper.Sdk.Ssh;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>iDRAC services: Redfish status and power, the HTML5 virtual console and the web interface, at the iDRAC's own address.</summary>
public sealed class IdracTests(PostgresServerFixture postgres, RedfishTargetFixture idrac) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        idrac.PluginType = "eHTML5";
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task IdracService_RequiresAValidAddress_AndKeepsItNormalized()
    {
        var missing = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "r750",
            ipAddress = "10.0.0.1",
            services = new object[] { new { protocol = "Idrac" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("services[0].address", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var invalid = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "r750",
            ipAddress = "10.0.0.1",
            services = new object[] { new { protocol = "Idrac", address = "not an address!" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var created = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "r750",
            ipAddress = "10.0.0.1",
            services = new object[] { new { protocol = "Idrac", address = " 2001:DB8::10 " }, new { protocol = "Ssh", address = "10.9.9.9" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var host = (await created.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
        Assert.Equal("2001:db8::10", host.Services[0].Address);
        // Only iDRAC services have their own address.
        Assert.Null(host.Services[1].Address);
    }

    [Fact]
    public async Task Overview_AsksToTrustTheIdracCertificate_ThenReportsTheServer()
    {
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/idrac/overview", Request(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        Assert.Equal(idrac.CertificateFingerprint, first.GetProperty("certificate").GetProperty("fingerprint").GetString());

        var ready = await PostAsync("/api/idrac/overview", Request(host, trust: idrac.CertificateFingerprint));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        var overview = ready.GetProperty("overview");
        Assert.Equal("PowerEdge R750", overview.GetProperty("model").GetString());
        Assert.Equal("ABC1234", overview.GetProperty("serviceTag").GetString());
        Assert.Equal("On", overview.GetProperty("powerState").GetString());
        Assert.Equal("Warning", overview.GetProperty("health").GetString());
        Assert.Equal(2, overview.GetProperty("processorCount").GetInt32());
        Assert.Equal(128, overview.GetProperty("logicalProcessorCount").GetInt32());
        Assert.Equal(512, overview.GetProperty("memoryGiB").GetDouble());
        Assert.Equal("7.00.00.171", overview.GetProperty("firmwareVersion").GetString());
        Assert.Contains("ForceRestart", overview.GetProperty("powerActions").EnumerateArray().Select(a => a.GetString()));
        Assert.True(overview.GetProperty("console").GetProperty("available").GetBoolean());
        Assert.Equal("eHTML5", overview.GetProperty("console").GetProperty("pluginType").GetString());

        // The certificate is trusted under the iDRAC, not the host's own HTTPS.
        var saved = await _admin.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct);
        var key = Assert.Single(saved!.TrustedKeys);
        Assert.Equal(ServiceProtocol.Idrac, key.Protocol);
        Assert.Equal(idrac.Port, key.Port);
        Assert.Equal("ready", (await PostAsync("/api/idrac/overview", Request(host))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task WrongPassword_IsReportedAsAnAuthenticationFailure()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/idrac/overview", Request(host, credential: host.Credentials[1].Id), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("authentication_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Power_PressesTheButtonsTheServerAccepts()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);
        while (idrac.Resets.TryDequeue(out _))
        {
        }

        var restart = await _admin.SendJsonAsync(HttpMethod.Post, "/api/idrac/power", Request(host, resetType: "ForceRestart"), Ct);
        Assert.True(restart.StatusCode == HttpStatusCode.OK, await restart.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["ForceRestart"], idrac.Resets.ToArray());

        var unsupported = await _admin.SendJsonAsync(HttpMethod.Post, "/api/idrac/power", Request(host, resetType: "GracefulRestart"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal(["ForceRestart"], idrac.Resets.ToArray());
    }

    [Fact]
    public async Task Console_OpensTheHtml5ConsoleWithOneTimeCredentials_ThroughNKeeper()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);

        var session = await PostAsync("/api/idrac/console", Request(host));
        Assert.Equal("ready", session.GetProperty("status").GetString());
        Assert.DoesNotContain(RedfishTargetFixture.TempPassword, session.GetRawText(), StringComparison.Ordinal);

        using var browser = ProxyClient(session.GetProperty("sessionId").GetString()!);
        var launch = await browser.GetAsync(session.GetProperty("launchPath").GetString(), Ct);
        Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);
        var console = launch.Headers.Location!.OriginalString;
        Assert.StartsWith("/console?username=root&tempUsername=kvm-temp-user&tempPassword=", console, StringComparison.Ordinal);

        Assert.Equal($"console for root as {RedfishTargetFixture.TempUsername} with {RedfishTargetFixture.TempPassword}", await browser.GetStringAsync(console, Ct));

        // The one-time credentials are handed out once: opening the link again lands on the iDRAC's home page.
        var again = await browser.GetAsync(session.GetProperty("launchPath").GetString(), Ct);
        Assert.Equal("/", again.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Console_ExplainsWhenTheIdracUsesAPluginThatCantRunInABrowser()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);
        idrac.PluginType = "Java";

        var overview = await PostAsync("/api/idrac/overview", Request(host));
        Assert.False(overview.GetProperty("overview").GetProperty("console").GetProperty("available").GetBoolean());

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/idrac/console", Request(host), Ct);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("HTML5", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebInterface_OpensTheIdracsOwnAddress()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);

        // The certificate trusted for Redfish is the web interface's too: no second prompt.
        var session = await PostAsync("/api/web/sessions", new { hostId = host.Id, serviceIndex = 0 });
        Assert.Equal("ready", session.GetProperty("status").GetString());

        using var browser = ProxyClient(session.GetProperty("sessionId").GetString()!);
        var launch = await browser.GetAsync(session.GetProperty("launchPath").GetString(), Ct);
        Assert.Equal("/", launch.Headers.Location!.OriginalString);
        Assert.Equal("iDRAC web interface", await browser.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task ChangingTheIdracAddress_ForgetsOnlyItsTrustedIdentities()
    {
        var host = await CreateHostAsync();
        await TrustAsync(host);

        var update = await _admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = host.Name,
            ipAddress = host.IpAddress,
            services = new object[] { new { protocol = "Idrac", address = "localhost", port = idrac.Port } },
            credentials = host.Credentials.Select(c => new { id = c.Id, username = c.Username }),
            version = host.Version,
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Empty((await update.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!.TrustedKeys);
    }

    [Fact]
    public async Task Racadm_IsSshToTheIdracsOwnAddress_WithItsKeysKeptApart()
    {
        var host = await CreateHostAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<SessionTargetResolver>();

        var target = await resolver.ResolveAsync(
            new SshStartMessage { HostId = host.Id, ServiceIndex = 0, CredentialId = host.Credentials[0].Id },
            new RequestUser(Guid.NewGuid(), IsAdministrator: true),
            ServiceProtocol.Ssh,
            defaultPort: 22,
            Ct);

        Assert.Equal("127.0.0.1", target.Address);
        // The service's port is the iDRAC's HTTPS port; RACADM is always on 22.
        Assert.Equal(22, target.Port);
        Assert.Equal(ServiceProtocol.Idrac, target.KeyProtocol);
        Assert.Equal(RedfishTargetFixture.Password, target.Password);
    }

    [Fact]
    public async Task NonIdracService_IsRejected()
    {
        var host = await CreateHostAsync();

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/idrac/overview", Request(host, serviceIndex: 1), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("wrong_service", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<HostResponse> CreateHostAsync()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"r750-{Guid.NewGuid():N}",
            // The server's own address; the iDRAC has its own.
            ipAddress = "192.0.2.10",
            services = new object[] { new { protocol = "Idrac", address = "127.0.0.1", port = idrac.Port }, new { protocol = "Ssh" } },
            credentials = new object[]
            {
                new { username = RedfishTargetFixture.Username, password = RedfishTargetFixture.Password },
                new { username = RedfishTargetFixture.Username, password = "wrong-password", label = "Wrong" },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private static object Request(HostResponse host, int serviceIndex = 0, Guid? credential = null, string? trust = null, string? resetType = null) => new
    {
        hostId = host.Id,
        serviceIndex,
        credentialId = credential ?? host.Credentials[0].Id,
        trustFingerprint = trust,
        resetType,
    };

    private async Task TrustAsync(HostResponse host) =>
        Assert.Equal("ready", (await PostAsync("/api/idrac/overview", Request(host, trust: idrac.CertificateFingerprint))).GetProperty("status").GetString());

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, path, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A browser on the web session's own origin.</summary>
    private HttpClient ProxyClient(string sessionId) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"https://nkw-{sessionId}.web.localhost/"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });
}
