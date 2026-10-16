using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>RDP sessions through a real guacd to a real xrdp server, with PostgreSQL.</summary>
public sealed class RdpSessionTests(PostgresServerFixture postgres, RdpServerFixture rdp) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        rdp.SkipIfUnavailable();
        _factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            settings: new Dictionary<string, string>
            {
                ["Guacd:Host"] = RdpServerFixture.Address,
                ["Guacd:Port"] = rdp.GuacdHostPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        (_client, _cookies) = await SignedInCookies.SignInAsync(_factory, Ct);
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
    public async Task FirstConnection_AsksToTrustTheCertificate_ThenStreamsTheDesktop()
    {
        var host = await CreateHostAsync(security: "Tls");

        await using (var session = await StartAsync(host))
        {
            await session.ExpectAsync("status", Ct);
            var prompt = await session.ExpectAsync("hostKey", Ct);
            Assert.Equal("unknown", prompt.GetProperty("status").GetString());
            Assert.Equal("certificate", prompt.GetProperty("kind").GetString());
            Assert.Equal(rdp.CertificateFingerprint, prompt.GetProperty("fingerprint").GetString());
            Assert.Contains("nkeeper-xrdp-test", prompt.GetProperty("subject").GetString(), StringComparison.Ordinal);

            await session.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
            await session.ExpectAsync("connected", Ct);
            await session.WaitForInstructionAsync("ready", Ct);
            await session.WaitForInstructionAsync("img", Ct);
            Assert.Null(session.GuacamoleError);
        }

        var key = Assert.Single((await GetHostAsync(host.Id)).TrustedKeys);
        Assert.Equal(ServiceProtocol.Rdp, key.Protocol);
        Assert.Equal(rdp.CertificateFingerprint, key.Fingerprint);

        // Trusted now: straight to the desktop.
        await using var second = await StartAsync(host);
        await second.ExpectAsync("status", Ct);
        await second.ExpectAsync("connected", Ct);
        await second.WaitForInstructionAsync("img", Ct);
    }

    [Fact]
    public async Task RejectingTheCertificate_EndsTheSessionAndTrustsNothing()
    {
        var host = await CreateHostAsync(security: "Tls");

        await using var session = await StartAsync(host);
        await session.ExpectAsync("status", Ct);
        await session.ExpectAsync("hostKey", Ct);
        await session.SendJsonAsync(new { type = "hostKeyDecision", trust = false }, Ct);

        Assert.Equal(SessionErrorCodes.HostKeyRejected, (await session.ExpectAsync("error", Ct)).GetProperty("code").GetString());
        Assert.Empty((await GetHostAsync(host.Id)).TrustedKeys);
    }

    [Fact]
    public async Task ChangedCertificate_IsRefusedBeforeReachingGuacd()
    {
        var host = await CreateHostAsync(security: "Tls");
        await TrustCertificateAsync(host);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().HostTrustedKeys
                .ExecuteUpdateAsync(k => k.SetProperty(x => x.Fingerprint, string.Join(':', Enumerable.Repeat("AB", 32))), Ct);
        }

        await using var session = await StartAsync(host);
        await session.ExpectAsync("status", Ct);
        var prompt = await session.ExpectAsync("hostKey", Ct);
        var error = await session.ExpectAsync("error", Ct);

        Assert.Equal("changed", prompt.GetProperty("status").GetString());
        Assert.Equal(SessionErrorCodes.HostKeyChanged, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequiringNla_OnAServerWithoutIt_ExplainsTheSetting()
    {
        var host = await CreateHostAsync(security: "Nla");

        await using var session = await StartAsync(host);
        await session.ExpectAsync("status", Ct);
        var error = await session.ExpectAsync("error", Ct);

        Assert.Equal(SessionErrorCodes.SecurityNegotiationFailed, error.GetProperty("code").GetString());
        Assert.Contains("set this service's security to TLS", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticSecurity_FallsBackToTls()
    {
        var host = await CreateHostAsync(security: null);

        await using var session = await StartAsync(host);
        await session.ExpectAsync("status", Ct);
        await session.ExpectAsync("hostKey", Ct);
        await session.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
        await session.ExpectAsync("connected", Ct);
        await session.WaitForInstructionAsync("img", Ct);
    }

    [Fact]
    public async Task UnreachableGateway_IsReportedClearly()
    {
        await using var factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            settings: new Dictionary<string, string> { ["Guacd:Host"] = "127.0.0.1", ["Guacd:Port"] = "1" });
        var (client, cookies) = await SignedInCookies.SignInAsync(factory, Ct);
        using var _ = client;
        var host = await CreateHostAsync(security: "Tls", http: client);

        await using var session = await TerminalClient.ConnectAsync(factory, cookies, Ct, protocol: "rdp");
        await session.SendJsonAsync(Start(host), Ct);
        await session.ExpectAsync("status", Ct);
        await session.ExpectAsync("hostKey", Ct);
        await session.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);

        Assert.Equal(SessionErrorCodes.GatewayUnavailable, (await session.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task KeyOnlyCredential_IsRejected()
    {
        var host = await CreateHostAsync(security: "Tls", credential: new { username = "rdpuser", privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----" });

        await using var session = await StartAsync(host);

        Assert.Equal(SessionErrorCodes.MissingSecret, (await session.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SshService_CannotBeOpenedAsRdp()
    {
        var host = await CreateHostAsync(security: null, protocol: "Ssh");

        await using var session = await StartAsync(host);

        Assert.Equal(SessionErrorCodes.WrongService, (await session.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SecuritySetting_RoundTripsAndOnlyAppliesToRdp()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "mixed",
            ipAddress = "10.0.0.5",
            services = new object[]
            {
                new { protocol = "Rdp", rdpSecurity = "Tls" },
                new { protocol = "Rdp" },
                new { protocol = "Ssh", rdpSecurity = "Nla" },
            },
        }, Ct);
        var host = (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;

        Assert.Equal([RdpSecurityMode.Tls, RdpSecurityMode.Auto, null], host.Services.Select(s => s.RdpSecurity));
    }

    private async Task<HostResponse> CreateHostAsync(string? security, object? credential = null, string protocol = "Rdp", HttpClient? http = null)
    {
        var response = await (http ?? _client).SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"rdp-{Guid.NewGuid():N}",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { protocol, port = (int?)rdp.Port, rdpSecurity = security } },
            credentials = new[] { credential ?? new { username = RdpServerFixture.UserName, password = RdpServerFixture.Password } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> GetHostAsync(Guid id) =>
        (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{id}", Json, Ct))!;

    private static object Start(HostResponse host) => new
    {
        type = "start",
        hostId = host.Id,
        serviceIndex = 0,
        credentialId = host.Credentials[0].Id,
        width = 1024,
        height = 768,
        dpi = 96,
        audio = new[] { "audio/L16;rate=44100,channels=2" },
        image = new[] { "image/png", "image/jpeg" },
        timezone = "Europe/Berlin",
    };

    private async Task<TerminalClient> StartAsync(HostResponse host)
    {
        var session = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "rdp");
        await session.SendJsonAsync(Start(host), Ct);
        return session;
    }

    private async Task TrustCertificateAsync(HostResponse host)
    {
        await using var session = await StartAsync(host);
        await session.ExpectAsync("status", Ct);
        await session.ExpectAsync("hostKey", Ct);
        await session.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
        await session.ExpectAsync("connected", Ct);
    }
}
