using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Telnet;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Telnet terminal sessions against a fake network device, and the protocol handling underneath.</summary>
public sealed class TelnetSessionTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeTelnetServer _device = new();
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
        _client.Dispose();
        await _factory.DisposeAsync();
        await _device.DisposeAsync();
    }

    [Fact]
    public async Task WithACredential_NKeeperLogsIn_AndNegotiatesTheTerminal()
    {
        var host = await CreateHostAsync(_device.Port, new { username = FakeTelnetServer.UserName, password = FakeTelnetServer.Password });

        await using var terminal = await StartAsync(host, host.Credentials[0].Id, cols: 120, rows: 40);
        Assert.Equal($"{FakeTelnetServer.UserName}@127.0.0.1:{_device.Port}", (await terminal.ExpectAsync("status", Ct)).GetProperty("target").GetString());
        await terminal.ExpectAsync("connected", Ct);
        await terminal.WaitForOutputAsync("Welcome alice", Ct);

        await terminal.SendInputAsync("show version\r", Ct);
        await terminal.WaitForOutputAsync("you said: show version", Ct);

        // xterm's size and type were sent; an option NKeeper doesn't support was declined.
        Assert.Equal("XTERM-256COLOR", _device.TerminalType);
        Assert.Equal((120, 40), _device.WindowSizes.First());
        Assert.Contains("WONT 39", _device.Negotiation);
        Assert.Contains("DO 1", _device.Negotiation);

        await terminal.SendJsonAsync(new { type = "resize", cols = 100, rows = 30 }, Ct);
        await terminal.SendInputAsync("ping\r", Ct);
        await terminal.WaitForOutputAsync("you said: ping", Ct);
        Assert.Equal((100, 30), _device.WindowSizes.Last());

        // The password was typed for the user, never shown in the terminal.
        Assert.DoesNotContain(FakeTelnetServer.Password, terminal.Output, StringComparison.Ordinal);

        await terminal.SendInputAsync("exit\r", Ct);
        await terminal.ExpectAsync("closed", Ct);
        await terminal.WaitForCloseAsync(Ct);
    }

    [Fact]
    public async Task WithoutACredential_TheUserLogsInInTheTerminal()
    {
        var host = await CreateHostAsync(_device.Port);

        await using var terminal = await StartAsync(host, credentialId: null);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("connected", Ct);
        await terminal.WaitForOutputAsync("login: ", Ct);

        await terminal.SendInputAsync($"{FakeTelnetServer.UserName}\r", Ct);
        await terminal.WaitForOutputAsync("Password: ", Ct);
        await terminal.SendInputAsync($"{FakeTelnetServer.Password}\r", Ct);
        await terminal.WaitForOutputAsync("Welcome alice", Ct);
    }

    [Fact]
    public async Task AHostThatIsntListening_IsReported()
    {
        using var unused = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var host = await CreateHostAsync(port);

        await using var terminal = await StartAsync(host, credentialId: null);
        await terminal.ExpectAsync("status", Ct);
        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Equal("connection_failed", error.GetProperty("code").GetString());
        Assert.Contains("refused", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_AreTakenOutOfTheData_EvenWhenSplitAcrossReads()
    {
        var protocol = new TelnetProtocol();
        var data = new List<byte>();
        var replies = new List<byte>();

        // "ab", an escaped 255, then DO NAWS split in the middle, then "c".
        protocol.Receive([(byte)'a', (byte)'b', 255, 255, 255, 253], data, replies);
        protocol.Receive([31, (byte)'c'], data, replies);

        Assert.Equal([(byte)'a', (byte)'b', 255, (byte)'c'], data);
        // WILL NAWS, then the window size (80 x 24 until resized).
        Assert.Equal([255, 251, 31, 255, 250, 31, 0, 80, 0, 24, 255, 240], replies);
    }

    [Fact]
    public void UnsupportedOptions_AreRefusedOnce_SoNegotiationCantLoop()
    {
        var protocol = new TelnetProtocol();
        var replies = new List<byte>();

        protocol.Receive([255, 253, 39, 255, 253, 39, 255, 251, 5], [], replies);

        Assert.Equal([255, 252, 39, 255, 254, 5], replies);
        Assert.False(protocol.ServerEchoes);
        protocol.Receive([255, 251, 1], [], replies = []);
        Assert.True(protocol.ServerEchoes);
        Assert.Equal([255, 253, 1], replies);
    }

    [Fact]
    public void TypedText_EscapesIac_AndEnterBecomesCrLf()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("ls\r\n"), TelnetProtocol.Encode(Encoding.ASCII.GetBytes("ls\r")));
        Assert.Equal(Encoding.ASCII.GetBytes("a\r\nb"), TelnetProtocol.Encode(Encoding.ASCII.GetBytes("a\r\nb")));
        Assert.Equal([1, 255, 255, 2], TelnetProtocol.Encode([1, 255, 2]));
    }

    private async Task<HostResponse> CreateHostAsync(int port, object? credential = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"switch-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { protocol = "Telnet", port } },
            credentials = credential is null ? [] : new[] { credential },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<TerminalClient> StartAsync(HostResponse host, Guid? credentialId, int cols = 80, int rows = 24)
    {
        var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "telnet");
        await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId, cols, rows }, Ct);
        return terminal;
    }
}
