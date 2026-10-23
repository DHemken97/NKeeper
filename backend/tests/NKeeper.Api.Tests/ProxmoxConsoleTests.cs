using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Controllers;
using NKeeper.Api.Proxmox;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Proxmox VMs and containers through the Proxmox API (a fake one): certificate trust, signing in
/// with a password or an API token, consoles (the VNC proxy's WebSocket relayed to the browser's
/// channel), power actions, migrations, and following their tasks.
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

    [Fact]
    public async Task Guest_ShowsItsStateAndTheNodesToMigrateTo()
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));

        var answer = await PostAsync("/api/proxmox/guest", Request(host, vm: 105));

        Assert.Equal(new ProxmoxGuestInfo(105, "qemu", "pve2", "app01", "running"), answer.GetProperty("guest").Deserialize<ProxmoxGuestInfo>(Json));
        Assert.Equal(
            [new ProxmoxNode("pve1", true), new ProxmoxNode("pve2", true), new ProxmoxNode("pve3", false)],
            answer.GetProperty("nodes").Deserialize<List<ProxmoxNode>>(Json));
    }

    [Theory]
    [InlineData(105, "shutdown", "nodes/pve2/qemu/105/status/shutdown")]
    [InlineData(105, "reboot", "nodes/pve2/qemu/105/status/reboot")]
    [InlineData(105, "pause", "nodes/pve2/qemu/105/status/suspend")]
    [InlineData(105, "hibernate", "nodes/pve2/qemu/105/status/suspend?todisk=1")]
    [InlineData(105, "resume", "nodes/pve2/qemu/105/status/resume")]
    [InlineData(105, "stop", "nodes/pve2/qemu/105/status/stop")]
    [InlineData(105, "reset", "nodes/pve2/qemu/105/status/reset")]
    [InlineData(101, "start", "nodes/pve1/qemu/101/status/start")]
    [InlineData(300, "pause", "nodes/pve1/lxc/300/status/suspend")]
    public async Task PowerActions_StartProxmoxTasks_ThatCanBeFollowed(int vm, string action, string expected)
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));

        var answer = await PostAsync("/api/proxmox/power", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm, action });

        Assert.Equal(expected, pve.Tasks.Last());
        var upid = answer.GetProperty("upid").GetString()!;
        Assert.StartsWith("UPID:", upid, StringComparison.Ordinal);
        Assert.Equal(new ProxmoxTaskStatus(true, null), await TaskStatusAsync(host, upid));
        Assert.Equal(new ProxmoxTaskStatus(false, "OK"), await TaskStatusAsync(host, upid));
    }

    [Theory]
    [InlineData(105, "start", "was already running")]
    [InlineData(101, "shutdown", "was already off")]
    [InlineData(101, "stop", "was already off")]
    public async Task PowerActions_ToTheStateTheGuestIsIn_AreDone_WithoutATask(int vm, string action, string already)
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));
        var tasks = pve.Tasks.Count;

        var answer = await PostAsync("/api/proxmox/power", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm, action });

        Assert.Equal(already, answer.GetProperty("already").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("upid").ValueKind);
        Assert.Equal(tasks, pve.Tasks.Count);
    }

    [Fact]
    public async Task AStartTask_ThatFoundTheVmRunning_EndsOk()
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));
        const string upid = "UPID:pve1:000ABCDE:00123456:6720F00D:qmstart:101:root@pam:";

        await TaskStatusAsync(host, upid);

        Assert.Equal(new ProxmoxTaskStatus(false, "OK", "was already running"), await TaskStatusAsync(host, upid));
    }

    [Theory]
    [InlineData("start", "VM 101 already running", "was already running")]
    [InlineData("start", "Proxmox refused the request (500: CT 300 already running).", "was already running")]
    [InlineData("shutdown", "Proxmox refused the request (500: CT 300 not running).", "was already off")]
    [InlineData("stop", "VM 101 not running", "was already off")]
    [InlineData("start", "VM 101 not running", null)]
    [InlineData("reboot", "VM 101 not running", null)]
    [InlineData("start", "start failed: QEMU exited with code 1", null)]
    public void ProxmoxComplaints_ThatTheGuestIsAlreadyThere_AreRecognised(string action, string message, string? already) =>
        Assert.Equal(already, ProxmoxActions.AlreadyFromMessage(action, message));

    [Theory]
    [InlineData("UPID:pve1:000ABCDE:00123456:6720F00D:qmstart:101:root@pam:", "start")]
    [InlineData("UPID:pve1:000ABCDE:00123456:6720F00D:vzshutdown:300:root@pam:", "shutdown")]
    [InlineData("UPID:pve1:000ABCDE:00123456:6720F00D:qmigrate:101:root@pam:", null)]
    public void TaskIds_NameTheirPowerAction(string upid, string? action) => Assert.Equal(action, ProxmoxApi.TaskAction(upid));

    [Theory]
    [InlineData(300, "reset", "Containers can't be reset")]
    [InlineData(300, "hibernate", "Containers can't hibernate")]
    [InlineData(105, "explode", "Choose an action")]
    public async Task PowerActions_ThatCantBeDone_AreExplained(int vm, string action, string message)
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));
        var tasks = pve.Tasks.Count;

        var response = await _client.SendJsonAsync(
            HttpMethod.Post, "/api/proxmox/power", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm, action }, Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(tasks, pve.Tasks.Count);
    }

    [Theory]
    [InlineData(105, "pve1", true, true, "nodes/pve2/qemu/105/migrate?online=1&target=pve1&with-local-disks=1")]
    [InlineData(105, "pve1", false, false, "nodes/pve2/qemu/105/migrate?target=pve1")]
    [InlineData(300, "pve2", true, true, "nodes/pve1/lxc/300/migrate?restart=1&target=pve2")]
    // A stopped VM moves offline, whatever was asked.
    [InlineData(101, "pve2", true, false, "nodes/pve1/qemu/101/migrate?target=pve2")]
    public async Task Migrations_MoveGuestsToAnotherNode(int vm, string target, bool online, bool withLocalDisks, string expected)
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));

        var answer = await PostAsync("/api/proxmox/migrate", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm, target, online, withLocalDisks });

        Assert.Equal(expected, pve.Tasks.Last());
        var upid = answer.GetProperty("upid").GetString()!;
        await TaskStatusAsync(host, upid);
        Assert.Equal(new ProxmoxTaskStatus(false, "migration aborted"), await TaskStatusAsync(host, upid));
    }

    [Theory]
    [InlineData("pve2", "already on pve2")]
    [InlineData("pve3", "is offline")]
    [InlineData("pve9", "There's no node “pve9”")]
    public async Task Migrations_ThatCantBeDone_AreExplained(string target, string message)
    {
        var host = await CreateTrustedHostAsync(("root", ProxmoxApiFixture.Password));

        var response = await _client.SendJsonAsync(
            HttpMethod.Post, "/api/proxmox/migrate", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm = 105, target, online = true }, Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tasks_AskToTrustTheCertificateFirst()
    {
        var host = await CreateHostAsync(("root", ProxmoxApiFixture.Password));

        var answer = await PostAsync("/api/proxmox/power", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, vm = 105, action = "stop" });

        Assert.Equal("trustRequired", answer.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(answer.GetProperty("certificate").GetProperty("fingerprint").GetString()));
    }

    [Theory]
    [InlineData("UPID:pve1:000ABCDE:00123456:6720F00D:qmstop:105:root@pam:", "pve1")]
    [InlineData("UPID:../x:0", null)]
    [InlineData("pve1", null)]
    public void TaskIds_NameTheirNode(string upid, string? node) => Assert.Equal(node, ProxmoxApi.TaskNode(upid));

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

    private Task<JsonElement> PostAsync(object body) => PostAsync("/api/proxmox/consoles", body);

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, path, body, Ct);
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

    private async Task<ProxmoxTaskStatus> TaskStatusAsync(HostResponse host, string upid) =>
        (await PostAsync("/api/proxmox/task", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, upid }))
            .GetProperty("task").Deserialize<ProxmoxTaskStatus>(Json)!;

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
