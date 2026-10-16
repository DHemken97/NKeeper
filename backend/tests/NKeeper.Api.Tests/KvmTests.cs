using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Kvm;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// KVM hosts over SSH: VM lists from (a fake) virsh, and SPICE consoles tunnelled through the
/// real SSH connection to a stand-in SPICE port on the server's loopback.
/// </summary>
public sealed class KvmTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly Guid RunningVm = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StoppedVm = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        ssh.SkipIfUnavailable();
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
    public async Task ListingVms_AsksToTrustTheHostKey_ThenReturnsThem()
    {
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/kvm/vms", Request(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var fingerprint = first.GetProperty("hostKey").GetProperty("fingerprint").GetString();

        var listed = await PostAsync("/api/kvm/vms", Request(host, trust: fingerprint));
        var vms = listed.GetProperty("vms").Deserialize<List<VirtualMachine>>(Json)!;

        Assert.Equal(["db-vm", "web vm"], vms.Select(v => v.Name));
        Assert.Equal(new VirtualMachine(RunningVm, "web vm", "running", "spice"), vms[1]);
        Assert.Equal(new VirtualMachine(StoppedVm, "db-vm", "shut off", null), vms[0]);

        // The key is now a trusted SSH host key, shared with SSH sessions.
        var stored = (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct))!;
        Assert.Equal(NKeeper.Infrastructure.Inventory.ServiceProtocol.Ssh, Assert.Single(stored.TrustedKeys).Protocol);
        Assert.Equal("ready", (await PostAsync("/api/kvm/vms", Request(host))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task SpiceConsole_TunnelsChannelsThroughSsh()
    {
        var host = await CreateTrustedHostAsync();

        var console = await PostAsync("/api/spice/sessions", Request(host, vm: RunningVm));
        Assert.Equal("ready", console.GetProperty("status").GetString());
        Assert.Equal("vm-secret", console.GetProperty("password").GetString());

        using var channel = await OpenChannelAsync(console.GetProperty("sessionId").GetString()!);
        Assert.Equal("binary", channel.SubProtocol);
        await channel.SendAsync(Encoding.ASCII.GetBytes("REDQ-spice-link"), WebSocketMessageType.Binary, true, Ct);
        var received = new StringBuilder();
        var buffer = new byte[1024];
        while (received.Length < "REDQ-spice-link".Length)
        {
            var result = await channel.ReceiveAsync(buffer, Ct);
            received.Append(Encoding.ASCII.GetString(buffer, 0, result.Count));
        }

        Assert.Equal("REDQ-spice-link", received.ToString());
        await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/spice/sessions/{console.GetProperty("sessionId").GetString()}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task StoppedVm_HasNoConsole()
    {
        var host = await CreateTrustedHostAsync();

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/spice/sessions", Request(host, vm: StoppedVm), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("not running", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherUsers_CannotUseTheConsole_OrAHostTheyCantSee()
    {
        var host = await CreateTrustedHostAsync();
        var console = await PostAsync("/api/spice/sessions", Request(host, vm: RunningVm));
        await _client.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        var (bob, bobCookies) = await SignInAsAsync("bob", "Bob-Passw0rd-123");
        using var _ = bob;

        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendJsonAsync(HttpMethod.Post, "/api/kvm/vms", Request(host), Ct)).StatusCode);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => OpenChannelAsync(console.GetProperty("sessionId").GetString()!, bobCookies));
    }

    [Theory]
    [InlineData("spice://127.0.0.1:5900", "127.0.0.1", 5900, null)]
    [InlineData("spice://127.0.0.1:5901/?password=s3cret", "127.0.0.1", 5901, "s3cret")]
    [InlineData("spice://:p%40ss@localhost:5902", "localhost", 5902, "p@ss")]
    [InlineData("spice://0.0.0.0:5903", "127.0.0.1", 5903, null)]
    [InlineData("spice://[::1]:5904", "::1", 5904, null)]
    public void SpiceUris_AreParsed(string uri, string host, int port, string? password) =>
        Assert.Equal(new SpiceDisplay(host, port, password), KvmHost.ParseSpiceUri(uri));

    [Fact]
    public void TlsOnlySpice_IsExplained()
    {
        var error = Assert.Throws<SessionException>(() => KvmHost.ParseSpiceUri("spice://127.0.0.1?tls-port=5901"));
        Assert.Contains("TLS", error.Message, StringComparison.Ordinal);
    }

    private static object Request(HostResponse host, string? trust = null, Guid? vm = null) =>
        new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = trust, vm };

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
            name = $"kvm-{Guid.NewGuid():N}",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { protocol = "Kvm", port = (int?)ssh.Port } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateTrustedHostAsync()
    {
        var host = await CreateHostAsync();
        var first = await PostAsync("/api/kvm/vms", Request(host));
        await PostAsync("/api/kvm/vms", Request(host, trust: first.GetProperty("hostKey").GetProperty("fingerprint").GetString()));
        return host;
    }

    private async Task<WebSocket> OpenChannelAsync(string sessionId, CookieContainer? cookies = null)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add("binary");
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = (cookies ?? _cookies).GetCookieHeader(new Uri("http://localhost/"));
            request.Headers.Origin = "http://localhost";
        };
        return await client.ConnectAsync(new Uri($"ws://localhost/api/spice/channel/{sessionId}"), Ct);
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsAsync(string userName, string password)
    {
        var cookies = new CookieContainer();
        var client = _factory.CreateDefaultClient(new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(cookies));
        (await client.LoginAsync(userName, password, Ct)).EnsureSuccessStatusCode();
        return (client, cookies);
    }
}
