using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;
/// <summary>Network (subnet) scans: parsing the target, the probes' parsers, and matching found machines to hosts.</summary>
public sealed class NetworkScanLogicTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24", 254)]
    [InlineData("192.168.1.77/24", "192.168.1.0/24", 254)]
    [InlineData(" 10.0.0.5 ", "10.0.0.5/32", 1)]
    [InlineData("10.0.0.4/31", "10.0.0.4/31", 2)]
    [InlineData("10.0.16.0/20", "10.0.16.0/20", 4094)]
    public void Subnet_Parses_ToItsNetwork(string text, string expected, int count)
    {
        Assert.True(Subnet.TryParse(text, out var subnet, out var error), error);
        Assert.Equal(expected, subnet.ToString());
        Assert.Equal(count, subnet.Count);
        Assert.Equal(count, subnet.Addresses().Count());
    }

    [Theory]
    [InlineData("10.0.0.0/16", "at most 4,096")]
    [InlineData("10.0.0.0/33", "prefix length")]
    [InlineData("server01/24", "CIDR")]
    [InlineData("10.1/24", "CIDR")]
    [InlineData("fe80::/120", "IPv4")]
    public void Subnet_RefusesWhatItCantSweep(string text, string reason)
    {
        Assert.False(Subnet.TryParse(text, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Subnet_LeavesOutTheNetworkAndBroadcastAddresses()
    {
        Assert.True(Subnet.TryParse("10.0.0.0/30", out var subnet, out _));
        Assert.Equal(["10.0.0.1", "10.0.0.2"], subnet.Addresses().Select(a => a.ToString()));
    }

    [Fact]
    public void NetBios_ReadsTheNameWorkgroupAndMac()
    {
        const ushort transaction = 0x1234;
        var request = NetworkProbes.NodeStatusRequest(transaction);
        Assert.Equal(50, request.Length);
        Assert.Equal("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Encoding.ASCII.GetString(request, 13, 32));

        // The answer echoes the question's name, then lists the machine's names and its MAC address.
        var response = new List<byte>
        {
            0x12, 0x34, 0x84, 0x00, 0, 0, 0, 1, 0, 0, 0, 0,
        };
        response.AddRange(request.Skip(12).Take(34)); // the name
        response.AddRange([0, 0x21, 0, 1, 0, 0, 0, 0, 0, 0]); // type, class, TTL, data length (unchecked)
        response.Add(3);
        response.AddRange(Entry("FILESRV01", 0x00, group: false));
        response.AddRange(Entry("CORP", 0x00, group: true));
        response.AddRange(Entry("FILESRV01", 0x20, group: false));
        response.AddRange([0x00, 0x15, 0x5D, 0x01, 0x02, 0x03]);

        var answer = NetworkProbes.ParseNodeStatus([.. response], transaction);

        Assert.Equal(new NetworkProbes.NetBiosAnswer("FILESRV01", "CORP", "00:15:5D:01:02:03"), answer);
        Assert.Null(NetworkProbes.ParseNodeStatus([.. response], 0x9999));

        static byte[] Entry(string name, byte suffix, bool group) =>
            [.. Encoding.ASCII.GetBytes(name.PadRight(15)), suffix, (byte)(group ? 0x84 : 0x04), 0x00];
    }

    [Theory]
    [InlineData("00:50:56:AA:BB:CC", "VMware", true)]
    [InlineData("bc:24:11:aa:bb:cc", "Proxmox VE", true)]
    [InlineData("02:42:AC:11:00:02", "Docker container", true)]
    [InlineData("B8:27:EB:00:00:01", "Raspberry Pi", false)]
    [InlineData("DA:A1:19:00:00:01", "Randomized or locally administered", false)]
    [InlineData("00:1B:21:00:00:01", null, false)]
    public void MacVendor_NamesKnownPrefixes(string mac, string? vendor, bool isVirtual) =>
        Assert.Equal((vendor, isVirtual), NetworkProbes.MacVendor(mac));

    [Fact]
    public void Probes_ReadTitles_AndTellHostnamesFromOtherNames()
    {
        Assert.Equal("Proxmox Virtual Environment", NetworkProbes.TitleOf("<html><head><TITLE>\n Proxmox  Virtual Environment </TITLE>"));
        Assert.Equal("Tom & Jerry", NetworkProbes.TitleOf("<title>Tom &amp; Jerry</title>"));
        Assert.Null(NetworkProbes.TitleOf("<html><body>no title</body></html>"));

        Assert.True(NetworkProbes.LooksLikeHostname("pve01.lab.example"));
        Assert.False(NetworkProbes.LooksLikeHostname("*.lab.example"));
        Assert.False(NetworkProbes.LooksLikeHostname("10.0.0.5"));
        Assert.False(NetworkProbes.LooksLikeHostname("localhost.localdomain"));

        Assert.Equal("Windows", NetworkProbes.TtlHint(127));
        Assert.Null(NetworkProbes.TtlHint(null));
    }

    [Fact]
    public void Matcher_PrefersMac_ThenIp_ThenHostname()
    {
        var web = Candidate("web01", "web01.corp.example", "10.0.0.30", "BC:24:11:AA:BB:CC");
        var db = Candidate("db01", null, "10.0.0.40", null);
        var matcher = new NetworkDeviceMatcher([web, db]);

        Assert.Equal((web, "mac"), matcher.Match("BC:24:11:AA:BB:CC", "10.0.0.40", []));
        Assert.Equal((db, "ip"), matcher.Match("00:11:22:33:44:55", "10.0.0.40", ["web01"]));
        Assert.Equal((web, "hostname"), matcher.Match(null, "10.0.0.99", ["WEB01.corp.example"]));
        Assert.Equal((db, "hostname"), matcher.Match(null, "10.0.0.99", ["db01.other.example"]));
        Assert.Null(matcher.Match(null, "10.0.0.99", ["app01"]));

        static GuestMatcher.Candidate Candidate(string name, string? hostname, string ip, string? mac) =>
            new(Guid.NewGuid(), name, hostname, mac, false, null, null, true, null, null, ip);
    }

    [Fact]
    public async Task WebProbe_ReadsTheServerAndTitle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(Ct);
            await socket.ReceiveAsync(new byte[4096], Ct);
            const string body = "<html><head><title>Printer status</title></head></html>";
            await socket.SendAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nServer: TestPrinter/2.1\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), Ct);
        }, Ct);

        var answer = await NetworkProbes.WebAsync(IPAddress.Loopback, port, https: false, Ct);
        await serve;

        Assert.NotNull(answer);
        Assert.Equal(200, answer.Status);
        Assert.Equal("TestPrinter/2.1", answer.Server);
        Assert.Equal("Printer status", answer.Title);
    }
}

/// <summary>Network scans through the API: their checks, who may run them, and a real scan of the loopback address.</summary>
public sealed class NetworkScanTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _client = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();


    [Fact]
    public async Task Scan_MustBeAcknowledged_AndSmallEnough()
    {
        var unacknowledged = await _client.SendJsonAsync(HttpMethod.Post, "/api/network-scan", new { subnet = "127.0.0.1/32", acknowledged = false }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unacknowledged.StatusCode);
        Assert.Contains("loud", await unacknowledged.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var tooLarge = await _client.SendJsonAsync(HttpMethod.Post, "/api/network-scan", new { subnet = "10.0.0.0/8", acknowledged = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("at most 4,096", await tooLarge.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_IsForAdministratorsOnly()
    {
        await _client.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        var response = await bob.SendJsonAsync(HttpMethod.Post, "/api/network-scan", new { subnet = "127.0.0.1/32", acknowledged = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Scan_OfLoopback_FindsTheMachine_MatchesItsHost_AndIsAudited()
    {
        var created = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "self", ipAddress = "127.0.0.1" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var started = await _client.SendJsonAsync(HttpMethod.Post, "/api/network-scan", new { subnet = "127.0.0.1/32", acknowledged = true }, Ct);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync(Ct));
        var scan = (await started.Content.ReadFromJsonAsync<NetworkScanResponse>(Json, Ct))!;
        Assert.Equal("127.0.0.1/32", scan.Subnet);
        Assert.Equal(1, scan.Total);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (scan.State == "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, Ct);
            scan = (await _client.GetFromJsonAsync<NetworkScanResponse>($"/api/network-scan/{scan.Id}", Json, Ct))!;
        }

        Assert.Equal("finished", scan.State);
        Assert.Equal(1, scan.Done);
        var device = Assert.Single(scan.Devices);
        Assert.Equal("127.0.0.1", device.IpAddress);
        Assert.NotEmpty(device.RespondedTo);
        Assert.Equal("self", device.MatchedHostName);
        Assert.Equal("ip", device.MatchedBy);
        Assert.Null(device.NewHost);

        var latest = (await _client.GetFromJsonAsync<NetworkScanResponse>("/api/network-scan/latest", Json, Ct))!;
        Assert.Equal(scan.Id, latest.Id);

        var audit = await _client.GetStringAsync("/api/audit/events?category=network", Ct);
        Assert.Contains("127.0.0.1/32", audit, StringComparison.Ordinal);
    }
}
