using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Proxmox;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Proxmox consoles through the Proxmox API (a fake one): certificate trust, signing in with a
/// password or an API token, and the VNC proxy's WebSocket relayed to the browser's channel.
/// </summary>
public sealed class ProxmoxConsoleTests(PostgresServerFixture postgres, ProxmoxApiFixture pve) : IAsyncLifetime, IClassFixture<ProxmoxApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
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
    public async Task Console_AsksToTrustTheCertificate_ThenRelaysTheVncProxy()
    {
        var host = await CreateHostAsync(("root", ProxmoxApiFixture.Password));

        var first = await PostAsync(Request(host, vm: 105));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var fingerprint = first.GetProperty("certificate").GetProperty("fingerprint").GetString();

        var console = await PostAsync(Request(host, vm: 105, trust: fingerprint));
        Assert.Equal("ready", console.GetProperty("status").GetString());
        // The VNC password is the proxy's ticket, as in Proxmox's own console.
        var ticket = console.GetProperty("password").GetString()!;
        Assert.StartsWith("PVEVNC:", ticket, StringComparison.Ordinal);
        var id = console.GetProperty("sessionId").GetString()!;

        using var channel = await OpenChannelAsync($"/api/vnc/channel/{id}");
        Assert.Equal("vnc qemu/105 on pve2\n", await ReadLineAsync(channel));
        Assert.Equal("RFB 003.008\n", await EchoAsync(channel, "RFB 003.008\n"));
        await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        Assert.DoesNotContain(ticket, pve.OpenProxies.Keys);

        // The certificate is now the host's trusted HTTPS certificate, shared with its web sessions.
        var stored = (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct))!;
        Assert.Equal(NKeeper.Infrastructure.Inventory.ServiceProtocol.Https, Assert.Single(stored.TrustedKeys).Protocol);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/vnc/consoles/{id}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task ContainerConsoles_AndApiTokens_Work()
    {
        var host = await CreateTrustedHostAsync((ProxmoxApiFixture.TokenId, ProxmoxApiFixture.TokenSecret));

        var console = await PostAsync(Request(host, vm: 300));

        using var channel = await OpenChannelAsync($"/api/vnc/channel/{console.GetProperty("sessionId").GetString()}");
        Assert.Equal("vnc lxc/300 on pve1\n", await ReadLineAsync(channel));
        await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Theory]
    [InlineData("root", ProxmoxApiFixture.Password, 101, "not running")]
    [InlineData("root", ProxmoxApiFixture.Password, 999, "no VM or container 999")]
    [InlineData("root", "wrong", 105, "refused the login root@pam")]
    [InlineData("tfa@pve", ProxmoxApiFixture.Password, 105, "two-factor authentication")]
    public async Task ConsolesThatCantOpen_AreExplained(string username, string password, int vm, string message)
    {
        var host = await CreateTrustedHostAsync((username, password));

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/proxmox/consoles", Request(host, vm: vm), Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAProxmoxService_OpensProxmoxConsoles()
    {
        var host = await CreateHostAsync(("root", ProxmoxApiFixture.Password), protocol: "Https");

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/proxmox/consoles", Request(host, vm: 105), Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("not an PROXMOX service", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("root", null, "root@pam")]
    [InlineData("admin", "pve", "admin@pve")]
    [InlineData("admin@ldap", "pve", "admin@ldap")]
    [InlineData("root!nkeeper", null, "root@pam!nkeeper")]
    [InlineData("ops@pve!ci", "pam", "ops@pve!ci")]
    public void Logins_BecomeProxmoxUserIds(string username, string? domain, string expected) =>
        Assert.Equal(expected, ProxmoxApi.UserId(username, domain));

    private static object Request(HostResponse host, int vm, string? trust = null) =>
        new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = trust, vm };

    private async Task<JsonElement> PostAsync(object body)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/proxmox/consoles", body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HostResponse> CreateHostAsync((string Username, string Password) login, string protocol = "Proxmox")
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"pve-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { protocol, port = (int?)pve.Port } },
            credentials = new[] { new { username = login.Username, password = login.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateTrustedHostAsync((string Username, string Password) login)
    {
        var host = await CreateHostAsync(login);
        var first = await PostAsync(Request(host, vm: 105));
        var fingerprint = first.GetProperty("certificate").GetProperty("fingerprint").GetString();
        await _client.SendJsonAsync(HttpMethod.Post, "/api/proxmox/consoles", Request(host, vm: 105, trust: fingerprint), Ct);
        return host;
    }

    private static async Task<string> ReadLineAsync(WebSocket channel)
    {
        var received = new StringBuilder();
        var buffer = new byte[1];
        while (!received.ToString().EndsWith('\n'))
        {
            var result = await channel.ReceiveAsync(buffer, Ct);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            received.Append(Encoding.ASCII.GetString(buffer, 0, result.Count));
        }

        return received.ToString();
    }

    private static async Task<string> EchoAsync(WebSocket channel, string text)
    {
        await channel.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Binary, true, Ct);
        var received = new StringBuilder();
        var buffer = new byte[1024];
        while (received.Length < text.Length)
        {
            var result = await channel.ReceiveAsync(buffer, Ct);
            received.Append(Encoding.ASCII.GetString(buffer, 0, result.Count));
        }

        return received.ToString();
    }

    private async Task<WebSocket> OpenChannelAsync(string path)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add("binary");
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = _cookies.GetCookieHeader(new Uri("http://localhost/"));
            request.Headers.Origin = "http://localhost";
        };
        return await client.ConnectAsync(new Uri($"ws://localhost{path}"), Ct);
    }
}
