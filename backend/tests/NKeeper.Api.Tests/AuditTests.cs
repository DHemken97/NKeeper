using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Auditing;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>The audit trail: what's recorded, terminal recordings, who can read it, and retention.</summary>
public sealed class AuditTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeTelnetServer _device = new();
    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        (_admin, _cookies) = await SignedInCookies.SignInAsync(_factory, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _device.DisposeAsync();
    }

    [Fact]
    public async Task SignIns_AndHostChanges_AreRecorded_WithNamesThatOutliveTheHost()
    {
        using (var stranger = _factory.CreateClient())
        {
            await stranger.LoginAsync("mallory", "guess-1", Ct);
        }

        var host = await CreateHostAsync("web01");
        var update = await _admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "web01-renamed", ipAddress = "10.0.0.8", version = host.Version }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}", null, Ct)).StatusCode);
        // A refused change isn't recorded as one.
        await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "" }, Ct);

        var events = await EventsAsync();
        Assert.Contains(events, e => e.GetProperty("action").GetString() == AuditActions.SignInFailed && e.GetProperty("userName").GetString() == "mallory");
        Assert.Contains(events, e => e.GetProperty("action").GetString() == AuditActions.SignIn && e.GetProperty("userName").GetString() == NKeeperApiFactory.AdminUserName);
        var hostEvents = events.Where(e => e.GetProperty("action").GetString()!.StartsWith("host.", StringComparison.Ordinal))
            .Select(e => (e.GetProperty("action").GetString(), e.GetProperty("hostName").GetString())).ToList();
        Assert.Equal([("host.delete", "web01-renamed"), ("host.update", "web01-renamed"), ("host.create", "web01")], hostEvents);
        Assert.All(events, e => Assert.False(e.GetProperty("detail").ValueKind == JsonValueKind.String && e.GetProperty("detail").GetString()!.Contains("guess-1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TerminalSessions_AreRecorded_AndCanBePlayedBack()
    {
        var host = await CreateHostAsync("switch01", services: [new { protocol = "Telnet", port = _device.Port }],
            credentials: [new { username = FakeTelnetServer.UserName, password = FakeTelnetServer.Password }]);

        await using (var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "telnet"))
        {
            await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, cols = 100, rows = 30 }, Ct);
            await terminal.ExpectAsync("status", Ct);
            await terminal.ExpectAsync("connected", Ct);
            await terminal.WaitForOutputAsync("Welcome alice", Ct);
            await terminal.SendJsonAsync(new { type = "resize", cols = 120, rows = 40 }, Ct);
            await terminal.SendInputAsync("show run\r", Ct);
            await terminal.WaitForOutputAsync("you said: show run", Ct);
            await terminal.SendInputAsync("exit\r", Ct);
            await terminal.ExpectAsync("closed", Ct);
            await terminal.WaitForCloseAsync(Ct);
        }

        var end = await WaitForEventAsync(AuditActions.SessionEnd);
        Assert.StartsWith("Telnet · alice@127.0.0.1:", end.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var start = (await EventsAsync()).Single(e => e.GetProperty("action").GetString() == AuditActions.SessionStart);
        Assert.Equal(start.GetProperty("sessionId").GetString(), end.GetProperty("sessionId").GetString());

        var recordingId = end.GetProperty("recordingId").GetString();
        var cast = await _admin.GetStringAsync($"/api/audit/recordings/{recordingId}/cast", Ct);
        var lines = cast.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal((2, 100, 30), (header.GetProperty("version").GetInt32(), header.GetProperty("width").GetInt32(), header.GetProperty("height").GetInt32()));
        Assert.Contains(lines, l => l.Contains("\"o\"", StringComparison.Ordinal) && l.Contains("you said: show run", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"r\",\"120x40\"", StringComparison.Ordinal));
        // Only output is recorded: the password NKeeper typed for the user isn't in it.
        Assert.DoesNotContain(FakeTelnetServer.Password, cast, StringComparison.Ordinal);

        var recording = await _admin.GetFromJsonAsync<JsonElement>($"/api/audit/recordings/{recordingId}", Ct);
        Assert.Equal(("Telnet", "switch01", false), (recording.GetProperty("protocol").GetString(), recording.GetProperty("hostName").GetString(), recording.GetProperty("truncated").GetBoolean()));
    }

    [Fact]
    public async Task SessionsThatCantConnect_AreRecordedAsFailed()
    {
        using var unused = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var host = await CreateHostAsync("dead01", services: [new { protocol = "Telnet", port }]);

        await using (var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "telnet"))
        {
            await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0 }, Ct);
            await terminal.ExpectAsync("status", Ct);
            await terminal.ExpectAsync("error", Ct);
        }

        var failed = await WaitForEventAsync(AuditActions.SessionFailed);
        Assert.Contains("refused", failed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("dead01", failed.GetProperty("hostName").GetString());
    }

    [Fact]
    public async Task TheTrail_CanBeFiltered_Paged_AndExported_ByAdministratorsOnly()
    {
        for (var i = 0; i < 5; i++)
        {
            await CreateHostAsync($"bulk{i:00}");
        }

        var page = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=host&limit=2", Ct);
        Assert.Equal(["bulk04", "bulk03"], page.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("hostName").GetString()));
        var next = page.GetProperty("next").GetInt64();
        var older = await _admin.GetFromJsonAsync<JsonElement>($"/api/audit/events?category=host&limit=2&before={next}", Ct);
        Assert.Equal(["bulk02", "bulk01"], older.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("hostName").GetString()));

        var search = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?q=BULK03", Ct);
        Assert.Equal("bulk03", Assert.Single(search.GetProperty("events").EnumerateArray()).GetProperty("hostName").GetString());

        // A spreadsheet must not run a formula smuggled into a name.
        await CreateHostAsync("=HYPERLINK(\"x\")");
        var csv = await _admin.GetStringAsync("/api/audit/events.csv?category=host", Ct);
        Assert.StartsWith("Time (UTC),User,Action,Host,Detail", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv, StringComparison.Ordinal);

        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/audit/events", Ct)).StatusCode);
    }

    [Fact]
    public async Task OldEvents_AndRecordings_AreRemovedByRetention()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        db.AuditEvents.Add(new AuditEvent { Action = "host.create", OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-400) });
        db.AuditEvents.Add(new AuditEvent { Action = "host.update", OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-10) });
        db.SessionRecordings.Add(new SessionRecording { Id = Guid.NewGuid(), SessionId = "old", Protocol = "SSH", StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100) });
        await db.SaveChangesAsync(Ct);

        await _factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AuditRetention>().Single().SweepAsync(Ct);

        Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == "host.create" && e.OccurredAtUtc < DateTimeOffset.UtcNow.AddDays(-365), Ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "host.update", Ct));
        Assert.False(await db.SessionRecordings.AnyAsync(Ct));
    }

    private async Task<HostResponse> CreateHostAsync(string name, object[]? services = null, object[]? credentials = null)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = "127.0.0.1", services = services ?? [], credentials = credentials ?? [] }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<List<JsonElement>> EventsAsync() =>
        [.. (await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?limit=500", Ct)).GetProperty("events").EnumerateArray()];

    /// <summary>Session ends are written as the session winds down, just after the socket closes.</summary>
    private async Task<JsonElement> WaitForEventAsync(string action)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if ((await EventsAsync()).FirstOrDefault(e => e.GetProperty("action").GetString() == action) is { ValueKind: JsonValueKind.Object } found)
            {
                return found;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"No {action} event was recorded.");
    }
}
