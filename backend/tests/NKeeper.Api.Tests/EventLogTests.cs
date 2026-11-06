using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.EventLogs;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>The WQL the viewer sends, what it makes of the answers, and reading the real WMI provider where there is one.</summary>
public sealed class EventLogLogicTests
{
    private static EventLogEntry Entry(long record, string? time = "20261106120000.000000-000") =>
        EventLogMapping.Entry(record, 7036, 3, "Service Control Manager", time, null, null, "SRV", null, "A message");

    private static SessionTarget Target(string username, string? domain = null) =>
        new(Guid.NewGuid(), "10.0.0.9", 135, new HostService(), username, domain, "secret", null, "test", [], ServiceProtocol.Rpc);

    [Fact]
    public void Wql_HasOnlyTheFiltersAsked_AndNoTimeClause()
    {
        var plain = new EventLogQuery("System", [], null, null, null, DateTimeOffset.UtcNow).ToWql();
        Assert.EndsWith("FROM Win32_NTLogEvent WHERE Logfile = 'System'", plain, StringComparison.Ordinal);

        var full = new EventLogQuery("Application", [EventLogLevels.Error, EventLogLevels.Information], "MsiInstaller", 1033, 500, null).ToWql();
        Assert.EndsWith(
            "WHERE Logfile = 'Application' AND (EventType = 1 OR EventType = 0 OR EventType = 3) AND SourceName = 'MsiInstaller' AND EventCode = 1033 AND RecordNumber < 500",
            full,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Wql_KeepsQuotesInsideTheirStrings()
    {
        var wql = new EventLogQuery(@"Bad\Log", [], "x' OR Logfile = 'Security", null, null, null).ToWql();

        Assert.Contains(@"Logfile = 'Bad\\Log'", wql, StringComparison.Ordinal);
        Assert.Contains(@"SourceName = 'x\' OR Logfile = \'Security'", wql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20261106182947.371480-000", "2026-11-06T18:29:47.3714800+00:00")]
    [InlineData("20261106182947.000000+060", "2026-11-06T18:29:47.0000000+01:00")]
    [InlineData("20261106182947.000000-300", "2026-11-06T18:29:47.0000000-05:00")]
    [InlineData("********182947.000000+000", null)]
    [InlineData("not a date", null)]
    [InlineData(null, null)]
    public void Dmtf_IsReadWithItsOffset(string? value, string? expected) =>
        Assert.Equal(expected, EventLogMapping.ParseDmtf(value)?.ToString("O"));

    [Fact]
    public void Message_FallsBackToTheEventsStrings()
    {
        Assert.Equal("Hello", EventLogMapping.Entry(1, 1, 1, null, null, null, null, null, ["unused"], "  Hello\r\n").Message);
        Assert.Equal($"a{Environment.NewLine}b", EventLogMapping.Entry(1, 1, 1, null, null, null, null, null, ["a", "", null, "b"], null).Message);
        Assert.Equal("This event has no message.", EventLogMapping.Entry(1, 1, 1, null, null, null, null, null, null, " ").Message);
    }

    [Theory]
    [InlineData(0L, "Information")]
    [InlineData(1L, "Error")]
    [InlineData(2L, "Warning")]
    [InlineData(3L, "Information")]
    [InlineData(4L, "Audit Success")]
    [InlineData(5L, "Audit Failure")]
    public void Level_ComesFromTheEventType(long type, string level) =>
        Assert.Equal(level, EventLogMapping.Entry(1, 1, type, null, null, null, null, null, null, "m").Level);

    [Theory]
    [InlineData(@"CORP\jane", null, "jane", "CORP")]
    [InlineData(@"CORP\jane", "OTHER", @"CORP\jane", "OTHER")]
    [InlineData("jane", null, "jane", null)]
    [InlineData("jane@corp.example", null, "jane@corp.example", null)]
    [InlineData(@"\jane", null, @"\jane", null)]
    public void Login_TypedAsDomainBackslashUser_IsSplit(string username, string? domain, string expectedUser, string? expectedDomain)
    {
        var split = EventLogMapping.SplitDomain(Target(username, domain));

        Assert.Equal((expectedUser, expectedDomain), (split.Username, split.Domain));
    }

    [Fact]
    public void Page_StopsReadingOnceFull_AndSaysMoreRemain()
    {
        var read = 0;
        IEnumerable<EventLogEntry> Rows()
        {
            for (var record = 1000L; ; record--)
            {
                read++;
                yield return Entry(record);
            }
        }

        var page = EventLogMapping.Page(Rows(), null);

        Assert.Equal(EventLogQuery.PageSize, page.Events.Count);
        Assert.True(page.HasMore);
        Assert.Equal(EventLogQuery.PageSize + 1, read);
    }

    [Fact]
    public void Page_StopsAtTheFirstOlderEvent_WithNothingMore()
    {
        var since = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        IEnumerable<EventLogEntry> Rows()
        {
            yield return Entry(3, "20261106130000.000000-000");
            yield return Entry(2, "20261106120000.000000-000");
            yield return Entry(1, "20261106110000.000000-000");
            throw new InvalidOperationException("Read past the time limit.");
        }

        var page = EventLogMapping.Page(Rows(), since);

        Assert.Equal([3L, 2L], page.Events.Select(e => e.RecordNumber));
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Page_OfAnExactlyFullLog_HasNoMore()
    {
        var page = EventLogMapping.Page(Enumerable.Range(0, EventLogQuery.PageSize).Select(i => Entry(i)), null);

        Assert.Equal(EventLogQuery.PageSize, page.Events.Count);
        Assert.False(page.HasMore);
    }

    /// <summary>The real provider, on this computer: WMI takes no login for local connections, so the login is left out.</summary>
    [Fact]
    public async Task WindowsReader_ReadsThisComputersLogs_NewestFirst()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs Windows.");
            return;
        }

        var reader = new WindowsEventLogReader();
        var target = Target(null!) with { Address = "localhost", Password = null };

        var logs = await reader.ListLogsAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains(logs, log => log.Name == "System" && log.Records > 0);

        var first = await reader.ReadAsync(target, new EventLogQuery("System", [], null, null, null, null), TestContext.Current.CancellationToken);
        Assert.NotEmpty(first.Events);
        var records = first.Events.Select(e => e.RecordNumber).ToList();
        Assert.Equal(records.OrderDescending(), records);
        Assert.All(first.Events, e => Assert.NotNull(e.TimeGenerated));
        Assert.All(first.Events, e => Assert.NotEmpty(e.Message));

        if (first.HasMore)
        {
            var older = await reader.ReadAsync(
                target, new EventLogQuery("System", [], null, null, records[^1], null), TestContext.Current.CancellationToken);
            Assert.True(older.Events[0].RecordNumber < records[^1]);
        }
    }
}

/// <summary>Who may read a host's event logs, with which login; the reading itself is faked.</summary>
public sealed class EventLogEndpointTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeReader _reader = new();
    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            configureServices: services => services.AddSingleton<IEventLogReader>(_reader));
        _client = await _factory.CreateSignedInClientAsync(Ct);
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
    public async Task Logs_UseTheSavedCredentialsLoginAndDomain()
    {
        var host = await CreateHostAsync();

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var logs = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("System", logs.GetProperty("logs")[0].GetProperty("name").GetString());
        Assert.Equal(("10.0.0.9", "administrator", "CORP", "S3cret!"), (_reader.Target!.Address, _reader.Target.Username, _reader.Target.Domain, _reader.Target.Password));
    }

    [Fact]
    public async Task Events_TakeATypedDomainLogin_AndPassTheFiltersOn()
    {
        var host = await CreateHostAsync();
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/events", new
        {
            hostId = host.Id,
            serviceIndex = 0,
            username = @"LAB\jane",
            password = "typed",
            log = "Application",
            levels = new[] { "Error", "Audit Failure" },
            source = "MsiInstaller",
            eventId = 1033,
            since,
        }, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(7036, page.GetProperty("events")[0].GetProperty("eventId").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(("jane", "LAB", "typed"), (_reader.Target!.Username, _reader.Target.Domain, _reader.Target.Password));
        Assert.Equal(new EventLogQuery("Application", _reader.Query!.Levels, "MsiInstaller", 1033, null, since), _reader.Query);
        Assert.Equal(["Error", "Audit Failure"], _reader.Query.Levels);
    }

    [Fact]
    public async Task ReadingALog_IsAudited_ButPagingBackThroughItIsnt()
    {
        var host = await CreateHostAsync();
        object Body(long? before) => new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, log = "System", beforeRecord = before };

        (await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/events", Body(null), Ct)).EnsureSuccessStatusCode();
        (await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/events", Body(100), Ct)).EnsureSuccessStatusCode();

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=eventlog", Ct);
        var entry = Assert.Single(audit.GetProperty("events").EnumerateArray());
        Assert.Equal(("eventlog.read", host.Name, "System log, as administrator"), (entry.GetProperty("action").GetString(), entry.GetProperty("hostName").GetString(), entry.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task AUserWithoutAccessToTheHost_IsToldItDoesntExist()
    {
        var host = await CreateHostAsync();
        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        var response = await bob.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_reader.Target);
    }

    [Fact]
    public async Task ASignedOutRequest_IsRefused()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = Guid.NewGuid(), serviceIndex = 0 }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AServiceThatIsntRpc_IsRefused()
    {
        var host = await CreateAsync(new
        {
            name = $"linux-{Guid.NewGuid():N}",
            ipAddress = "10.0.0.8",
            services = new[] { new { protocol = "Ssh" } },
            credentials = new[] { new { username = "root", password = "secret" } },
        });

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_reader.Target);
    }

    [Fact]
    public async Task ACredentialWithoutAPassword_IsRefused()
    {
        var host = await CreateAsync(new
        {
            name = $"win-{Guid.NewGuid():N}",
            ipAddress = "10.0.0.9",
            services = new[] { new { protocol = "Rpc" } },
            credentials = new[] { new { username = "administrator" } },
        });

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("needs a password", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostBehindAJumpHost_IsRefused()
    {
        var jump = await CreateAsync(new
        {
            name = $"jump-{Guid.NewGuid():N}",
            ipAddress = "10.0.0.7",
            services = new[] { new { protocol = "Ssh" } },
            credentials = new[] { new { username = "root", password = "secret" } },
        });
        var host = await CreateAsync(new
        {
            name = $"win-{Guid.NewGuid():N}",
            ipAddress = "10.0.0.9",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Rpc" } },
            credentials = new[] { new { username = "administrator", password = "S3cret!" } },
        });

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/eventlog/logs", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("jump host", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Null(_reader.Target);
    }

    [Fact]
    public async Task AnUnknownLevel_IsRefused()
    {
        var host = await CreateHostAsync();

        var response = await _client.SendJsonAsync(
            HttpMethod.Post, "/api/eventlog/events", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, log = "System", levels = new[] { "Oops" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_reader.Target);
    }

    [Fact]
    public async Task AFailedRead_ReportsWhy_AndIsntAudited()
    {
        var host = await CreateHostAsync();
        _reader.Failure = new SessionException(SessionErrorCodes.AuthenticationFailed, "Windows refused the login.");

        var response = await _client.SendJsonAsync(
            HttpMethod.Post, "/api/eventlog/events", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, log = "System" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Windows refused the login.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=eventlog", Ct);
        Assert.Empty(audit.GetProperty("events").EnumerateArray());
    }

    private Task<HostResponse> CreateHostAsync() => CreateAsync(new
    {
        name = $"win-{Guid.NewGuid():N}",
        ipAddress = "10.0.0.9",
        services = new[] { new { protocol = "Rpc" } },
        credentials = new[] { new { username = "administrator", domain = "CORP", password = "S3cret!" } },
    });

    private async Task<HostResponse> CreateAsync(object host)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", host, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private sealed class FakeReader : IEventLogReader
    {
        public SessionTarget? Target { get; private set; }

        public EventLogQuery? Query { get; private set; }

        public SessionException? Failure { get; set; }

        public Task<IReadOnlyList<EventLogInfo>> ListLogsAsync(SessionTarget target, CancellationToken cancellationToken)
        {
            Target = target;
            return Failure is null ? Task.FromResult<IReadOnlyList<EventLogInfo>>([new EventLogInfo("System", 12)]) : Task.FromException<IReadOnlyList<EventLogInfo>>(Failure);
        }

        public Task<EventLogPage> ReadAsync(SessionTarget target, EventLogQuery query, CancellationToken cancellationToken)
        {
            Target = target;
            Query = query;
            return Failure is null
                ? Task.FromResult(new EventLogPage([EventLogMapping.Entry(5, 7036, 3, "Service Control Manager", "20261106120000.000000-000", null, null, "SRV", null, "Started.")], true))
                : Task.FromException<EventLogPage>(Failure);
        }
    }
}
