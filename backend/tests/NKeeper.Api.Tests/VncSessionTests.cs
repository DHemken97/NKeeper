using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>VNC sessions through a real guacd to a real x11vnc server.</summary>
public sealed class VncSessionTests(PostgresServerFixture postgres, RdpServerFixture desktops) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        desktops.SkipIfUnavailable();
        _factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            settings: new Dictionary<string, string>
            {
                ["Guacd:Host"] = RdpServerFixture.Address,
                ["Guacd:Port"] = desktops.GuacdHostPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
    public async Task PasswordOnlyCredential_StreamsTheDesktop_WithoutATrustPrompt()
    {
        var host = await CreateHostAsync(new { label = "VNC password", password = RdpServerFixture.VncPassword });

        await using var session = await StartAsync(host, "vnc");

        Assert.Equal($"{RdpServerFixture.Address}:{desktops.VncPort}", (await session.ExpectAsync("status", Ct)).GetProperty("target").GetString());
        await session.ExpectAsync("connected", Ct);
        await session.WaitForInstructionAsync("img", Ct);
        Assert.Null(session.GuacamoleError);
    }

    [Fact]
    public async Task WrongPassword_IsReportedByTheGateway()
    {
        var host = await CreateHostAsync(new { label = "wrong", password = "not-the-password" });

        await using var session = await StartAsync(host, "vnc");
        await session.ExpectAsync("status", Ct);
        await session.ExpectAsync("connected", Ct);
        await session.WaitForInstructionAsync("error", Ct);

        Assert.NotNull(session.GuacamoleError);
    }

    [Theory]
    [InlineData("vnc", "Rdp")]
    [InlineData("rdp", "Vnc")]
    public async Task EachEndpoint_OnlyOpensItsOwnProtocol(string endpoint, string protocol)
    {
        var host = await CreateHostAsync(new { username = "u", password = "p" }, protocol);

        await using var session = await StartAsync(host, endpoint);

        Assert.Equal(SessionErrorCodes.WrongService, (await session.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    private async Task<HostResponse> CreateHostAsync(object credential, string protocol = "Vnc")
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"vnc-{Guid.NewGuid():N}",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { protocol, port = (int?)desktops.VncPort } },
            credentials = new[] { credential },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<TerminalClient> StartAsync(HostResponse host, string endpoint)
    {
        var session = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: endpoint);
        await session.SendJsonAsync(new
        {
            type = "start",
            hostId = host.Id,
            serviceIndex = 0,
            credentialId = host.Credentials[0].Id,
            width = 1024,
            height = 768,
            dpi = 96,
            image = new[] { "image/png" },
        }, Ct);
        return session;
    }
}
