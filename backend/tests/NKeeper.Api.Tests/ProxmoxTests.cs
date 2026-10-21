using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Kvm;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Proxmox VE nodes behind a KVM service: VM lists from (a fake) pvesh, and VNC consoles relayed
/// through (a fake) <c>qm vncproxy</c> over the real SSH connection, on this node or another.
/// </summary>
public sealed class ProxmoxTests(PostgresServerFixture postgres, ProxmoxServerFixture pve) : IAsyncLifetime, IClassFixture<ProxmoxServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        pve.SkipIfUnavailable();
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
    public async Task ListingVms_ShowsTheClustersVirtualMachines_WithTheirNodes()
    {
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/kvm/vms", Request(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var listed = await PostAsync("/api/kvm/vms", Request(host, trust: first.GetProperty("hostKey").GetProperty("fingerprint").GetString()));
        var vms = listed.GetProperty("vms").Deserialize<List<VirtualMachine>>(Json)!;

        // Containers and templates aren't listed; a serial terminal isn't a VNC display.
        Assert.Equal(
            [
                new VirtualMachine("101", "db", "stopped", "vnc", "pve-test"),
                new VirtualMachine("200", "remote", "running", "vnc", "pve2"),
                new VirtualMachine("102", "serial-vm", "running", null, "pve-test"),
                new VirtualMachine("100", "web", "running", "vnc", "pve-test"),
            ],
            vms);
    }

    [Fact]
    public async Task VncConsole_RelaysQmVncProxy_WithAOneTimeTicket()
    {
        var host = await CreateTrustedHostAsync();

        var console = await PostAsync("/api/vnc/consoles", Request(host, vm: "100"));
        Assert.Equal("ready", console.GetProperty("status").GetString());
        var ticket = console.GetProperty("password").GetString()!;
        Assert.Equal(8, ticket.Length);
        var id = console.GetProperty("sessionId").GetString()!;

        using (var channel = await OpenChannelAsync($"/api/vnc/channel/{id}"))
        {
            Assert.Equal($"vm 100 on pve-test ticket {ticket}\n", await ReadLineAsync(channel));
            Assert.Equal("RFB 003.008\n", await EchoAsync(channel, "RFB 003.008\n"));
            await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        }

        // Reconnecting runs the proxy again with the same ticket.
        using (var again = await OpenChannelAsync($"/api/vnc/channel/{id}"))
        {
            Assert.Equal($"vm 100 on pve-test ticket {ticket}\n", await ReadLineAsync(again));
            await again.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/vnc/consoles/{id}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task VncConsole_OfAVmOnAnotherNode_GoesThroughTheClustersSsh()
    {
        var host = await CreateTrustedHostAsync();

        var console = await PostAsync("/api/vnc/consoles", Request(host, vm: "200"));
        var ticket = console.GetProperty("password").GetString()!;

        using var channel = await OpenChannelAsync($"/api/vnc/channel/{console.GetProperty("sessionId").GetString()}");
        Assert.Equal($"vm 200 on 10.0.0.2 ticket {ticket}\n", await ReadLineAsync(channel));
        Assert.Equal("RFB 003.008\n", await EchoAsync(channel, "RFB 003.008\n"));
        await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Theory]
    [InlineData("/api/vnc/consoles", "102", "has no VNC display")]
    [InlineData("/api/vnc/consoles", "101", "not running")]
    [InlineData("/api/spice/sessions", "100", "has no SPICE display")]
    [InlineData("/api/vnc/consoles", "300", "no longer exists")]
    public async Task ConsolesThatCantOpen_AreExplained(string path, string vm, string message)
    {
        var host = await CreateTrustedHostAsync();

        var response = await _client.SendJsonAsync(HttpMethod.Post, path, Request(host, vm: vm), Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("std", true)]
    [InlineData("qxl,memory=64", true)]
    [InlineData("type=virtio,clipboard=vnc", true)]
    [InlineData("serial0", false)]
    [InlineData("type=serial1", false)]
    [InlineData("none", false)]
    public void VgaTypes_DecideWhetherThereIsAVncDisplay(string? vga, bool vnc) =>
        Assert.Equal(vnc, ProxmoxHost.HasVncDisplay(vga));

    private static object Request(HostResponse host, string? trust = null, string? vm = null) =>
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
            name = $"pve-{Guid.NewGuid():N}",
            ipAddress = ProxmoxServerFixture.Address,
            services = new[] { new { protocol = "Kvm", port = (int?)pve.Port } },
            credentials = new[] { new { username = ProxmoxServerFixture.UserName, password = ProxmoxServerFixture.Password } },
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
