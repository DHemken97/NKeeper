using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>HTTP/HTTPS sessions: NKeeper connects to the target and relays it on an isolated origin.</summary>
public sealed class WebSessionTests(PostgresServerFixture postgres, WebTargetFixture target) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" Web.Example.NET. ", "web.example.net")]
    public async Task BaseDomain_BlankMeansTheDefault(string configured, string? expected)
    {
        // Installers write WebProxy__BaseDomain even when it's empty.
        await using var factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct), settings: new Dictionary<string, string> { ["WebProxy:BaseDomain"] = configured });

        var settings = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NKeeper.Api.Configuration.WebProxySettings>>().Value;

        Assert.Equal(expected, settings.BaseDomain);
    }

    [Fact]
    public async Task HttpSession_RelaysTheTarget_AsIfBrowsedDirectly()
    {
        var host = await CreateHostAsync();
        var session = await OpenAsync(_admin, host, serviceIndex: 0);
        Assert.Equal("ready", session.GetProperty("status").GetString());

        using var browser = await LaunchAsync(session);
        Assert.Equal("hello from the target", await browser.GetStringAsync("/", Ct));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/headers");
        request.Headers.Add("Cookie", "theme=dark");
        request.Headers.Add("Origin", browser.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var headers = await (await browser.SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal($"127.0.0.1:{target.HttpPort}", headers.GetProperty("host").GetString());
        Assert.Equal($"http://127.0.0.1:{target.HttpPort}", headers.GetProperty("origin").GetString());
        Assert.DoesNotContain("__nkw_session", headers.GetProperty("cookie").GetString(), StringComparison.Ordinal);
        Assert.Contains("theme=dark", headers.GetProperty("cookie").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirects_AndCookies_StayOnTheSessionOrigin()
    {
        var host = await CreateHostAsync();
        using var browser = await LaunchAsync(await OpenAsync(_admin, host, serviceIndex: 0));

        var response = await browser.GetAsync("/login", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri(browser.BaseAddress!, "/home?welcome=1"), response.Headers.Location);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain("Domain", cookie, StringComparison.OrdinalIgnoreCase);
        // Usable inside NKeeper's frame (a different site).
        Assert.EndsWith("; Secure; SameSite=None; Partitioned", cookie, StringComparison.Ordinal);
        Assert.StartsWith("app_session=abc; Path=/; HttpOnly", cookie, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task OnlyNKeeper_MayFrameTheSession()
    {
        var host = await CreateHostAsync();
        var token = await _admin.GetAntiforgeryTokenAsync(Ct);
        using var open = new HttpRequestMessage(HttpMethod.Post, "/api/web/sessions") { Content = JsonContent.Create(new { hostId = host.Id, serviceIndex = 0 }) };
        open.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);
        open.Headers.Add("Origin", "https://nkeeper.example.com");
        var session = await (await _admin.SendAsync(open, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        using var browser = await LaunchAsync(session);

        var response = await browser.GetAsync("/framed", Ct);

        Assert.False(response.Headers.Contains("X-Frame-Options"));
        var policies = response.Headers.GetValues("Content-Security-Policy").ToList();
        Assert.Contains("default-src 'self'; img-src *", policies);
        Assert.Contains("frame-ancestors https://nkeeper.example.com", policies);
        Assert.DoesNotContain(policies, p => p.Contains("'none'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestBodies_AreForwarded()
    {
        var host = await CreateHostAsync();
        using var browser = await LaunchAsync(await OpenAsync(_admin, host, serviceIndex: 0));

        var response = await browser.PostAsync("/upload", new StringContent(new string('x', 200_000)), Ct);

        Assert.Equal("received 200000 chars", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SessionOrigin_RequiresTheLaunchCookie_AndNeverServesNKeeper()
    {
        var host = await CreateHostAsync();
        var session = await OpenAsync(_admin, host, serviceIndex: 0);
        using var stranger = ProxyClient(session.GetProperty("sessionId").GetString()!);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/", Ct)).StatusCode);

        using var browser = await LaunchAsync(session);
        // The launch link works once; a second browser can't reuse it.
        using var second = ProxyClient(session.GetProperty("sessionId").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync(session.GetProperty("launchPath").GetString(), Ct)).StatusCode);

        // NKeeper's API is not reachable on a session origin: the path goes to the target.
        var api = await browser.GetAsync("/api/hosts", Ct);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.DoesNotContain("hostname", await api.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var unknown = ProxyClient(new string('a', 32));
        Assert.Equal(HttpStatusCode.Gone, (await unknown.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task SelfSignedHttps_AsksToTrustTheCertificate_ThenPinsIt()
    {
        var host = await CreateHostAsync();

        var first = await OpenAsync(_admin, host, serviceIndex: 1);
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var fingerprint = first.GetProperty("certificate").GetProperty("fingerprint").GetString();
        Assert.Equal(target.CertificateFingerprint, fingerprint);

        var trusted = await OpenAsync(_admin, host, serviceIndex: 1, trustFingerprint: fingerprint);
        Assert.Equal("ready", trusted.GetProperty("status").GetString());
        using var browser = await LaunchAsync(trusted);
        Assert.Equal("https", (await browser.GetFromJsonAsync<JsonElement>("/headers", Ct)).GetProperty("scheme").GetString());

        // Trusted from now on: no prompt.
        Assert.Equal("ready", (await OpenAsync(_admin, host, serviceIndex: 1)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ChangedCertificate_IsRefused()
    {
        var host = await CreateHostAsync();
        var first = await OpenAsync(_admin, host, serviceIndex: 1);
        await OpenAsync(_admin, host, serviceIndex: 1, trustFingerprint: first.GetProperty("certificate").GetProperty("fingerprint").GetString());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().HostTrustedKeys
                .ExecuteUpdateAsync(k => k.SetProperty(x => x.Fingerprint, string.Join(':', Enumerable.Repeat("AB", 32))), Ct);
        }

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/web/sessions", new { hostId = host.Id, serviceIndex = 1 }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("host_key_changed", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheSession_EndsItOnItsOrigin()
    {
        var host = await CreateHostAsync();
        var session = await OpenAsync(_admin, host, serviceIndex: 0);
        using var browser = await LaunchAsync(session);

        var close = await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/web/sessions/{session.GetProperty("sessionId").GetString()}", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await browser.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task UsersWithoutAccess_CannotOpenTheHost_AndDisablingEndsTheirSessions()
    {
        var host = await CreateHostAsync();
        await _admin.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        var users = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/users", Ct);
        var bobId = users!.Single(u => u.GetProperty("userName").GetString() == "bob").GetProperty("id").GetString();
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        var denied = await bob.SendJsonAsync(HttpMethod.Post, "/api/web/sessions", new { hostId = host.Id, serviceIndex = 0 }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new { grants = new[] { new { hostId = host.Id, level = "Use" } } }, Ct);
        using var browser = await LaunchAsync(await OpenAsync(bob, host, serviceIndex: 0));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/", Ct)).StatusCode);

        await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bobId}", new { email = "bob@nkeeper.test", role = "User", isDisabled = true }, Ct);
        Assert.Equal(HttpStatusCode.Gone, (await browser.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task NonWebService_IsRejected()
    {
        var host = await CreateHostAsync();

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/web/sessions", new { hostId = host.Id, serviceIndex = 2 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HostResponse> CreateHostAsync()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"web-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new object[]
            {
                new { protocol = "Http", port = target.HttpPort },
                new { protocol = "Https", port = target.HttpsPort },
                new { protocol = "Ssh" },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private static async Task<JsonElement> OpenAsync(HttpClient client, HostResponse host, int serviceIndex, string? trustFingerprint = null)
    {
        var response = await client.SendJsonAsync(HttpMethod.Post, "/api/web/sessions", new { hostId = host.Id, serviceIndex, trustFingerprint }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A browser on the session's own origin, like the new tab NKeeper opens.</summary>
    private HttpClient ProxyClient(string sessionId) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"https://nkw-{sessionId}.web.localhost/"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private async Task<HttpClient> LaunchAsync(JsonElement session)
    {
        var browser = ProxyClient(session.GetProperty("sessionId").GetString()!);
        var launch = await browser.GetAsync(session.GetProperty("launchPath").GetString(), Ct);
        Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);
        return browser;
    }
}
