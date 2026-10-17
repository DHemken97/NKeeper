using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>An RDP session that logs in with the user's own domain sign-in, through a real guacd to a real xrdp server.</summary>
public sealed class RdpSignInPassThroughTests(PostgresServerFixture postgres, RdpServerFixture rdp) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly CookieContainer _cookies = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        rdp.SkipIfUnavailable();
        // The xrdp test account stands in for the user's domain account.
        var directory = new FakeDirectory().Add(RdpServerFixture.UserName, RdpServerFixture.Password, isAdministrator: true);
        var settings = FakeDirectory.Settings();
        settings["Guacd:Host"] = RdpServerFixture.Address;
        settings["Guacd:Port"] = rdp.GuacdHostPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), settings: settings, directory: directory);
        _client = _factory.CreateDefaultClient(new CookieContainerHandler(_cookies));
        (await _client.LoginAsync($@"{FakeDirectory.Domain}\{RdpServerFixture.UserName}", RdpServerFixture.Password, Ct)).EnsureSuccessStatusCode();
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
    public async Task SignInCredentials_LogIntoAHostWithNoSavedCredential()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"rdp-{Guid.NewGuid():N}",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { protocol = "Rdp", port = (int?)rdp.Port, rdpSecurity = "Tls" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hostId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        await using var session = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "rdp");
        await session.SendJsonAsync(new
        {
            type = "start",
            hostId,
            serviceIndex = 0,
            useSignInCredentials = true,
            width = 1024,
            height = 768,
            dpi = 96,
            image = new[] { "image/png" },
        }, Ct);

        var status = await session.ExpectAsync("status", Ct);
        Assert.StartsWith($"{RdpServerFixture.UserName}@", status.GetProperty("target").GetString(), StringComparison.Ordinal);
        await session.ExpectAsync("hostKey", Ct);
        await session.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
        await session.ExpectAsync("connected", Ct);
        await session.WaitForInstructionAsync("img", Ct);
        Assert.Null(session.GuacamoleError);
    }
}
