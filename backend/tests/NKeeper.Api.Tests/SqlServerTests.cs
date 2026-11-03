using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>SQL Server sessions against a real SQL Server: trust, queries, the object explorer, scripts and the blocking manager.</summary>
public sealed class SqlServerTests(PostgresServerFixture postgres, SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SqlServerService_KeepsANamedInstance_AndTheDefaultInstanceAsNone()
    {
        var invalid = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sql01",
            ipAddress = "10.0.0.1",
            services = new object[] { new { protocol = "Ssms", instance = "1bad name" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("services[0].instance", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var created = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sql01",
            ipAddress = "10.0.0.1",
            services = new object[]
            {
                new { protocol = "Ssms", instance = " SQLEXPRESS " },
                new { protocol = "Ssms", instance = "mssqlserver", port = 14330 },
                new { protocol = "Ssh", instance = "IGNORED" },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var host = (await created.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
        Assert.Equal(["SQLEXPRESS", null, null], host.Services.Select(s => s.Instance));
    }

    [Fact]
    public async Task Open_AsksToTrustTheServersCertificate_ThenSignsIn()
    {
        sql.SkipIfUnavailable();
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/mssql/sessions", Request(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var fingerprint = first.GetProperty("certificate").GetProperty("fingerprint").GetString()!;
        Assert.Contains("SSL_Self_Signed_Fallback", first.GetProperty("certificate").GetProperty("subject").GetString(), StringComparison.Ordinal);

        var ready = await PostAsync("/api/mssql/sessions", Request(host, trust: fingerprint));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal("sa", ready.GetProperty("server").GetProperty("login").GetString());
        Assert.StartsWith("16.", ready.GetProperty("server").GetProperty("productVersion").GetString(), StringComparison.Ordinal);
        Assert.Equal("master", ready.GetProperty("database").GetString());

        // Trusted under SQL Server on the service's port; the next session doesn't ask.
        var saved = await _admin.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct);
        var key = Assert.Single(saved!.TrustedKeys);
        Assert.Equal(ServiceProtocol.Ssms, key.Protocol);
        Assert.Equal(sql.Port, key.Port);
        Assert.Equal(fingerprint, key.Fingerprint);
        Assert.Equal("ready", (await PostAsync("/api/mssql/sessions", Request(host))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Open_AsksAgain_WhenSqlServerMadeItselfANewSelfSignedCertificate()
    {
        sql.SkipIfUnavailable();
        var host = await CreateHostAsync();
        await OpenAsync(host);

        // As after a restart: the trusted fallback certificate is not the one the server presents now.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeper.Infrastructure.Persistence.NKeeperDbContext>();
            var key = await db.HostTrustedKeys.SingleAsync(k => k.HostId == host.Id, Ct);
            key.Fingerprint = "00:11:22";
            await db.SaveChangesAsync(Ct);
        }

        var renewed = await PostAsync("/api/mssql/sessions", Request(host));
        Assert.Equal("trustRequired", renewed.GetProperty("status").GetString());
        Assert.True(renewed.GetProperty("renewed").GetBoolean());
        var fingerprint = renewed.GetProperty("certificate").GetProperty("fingerprint").GetString();

        Assert.Equal("ready", (await PostAsync("/api/mssql/sessions", Request(host, trust: fingerprint))).GetProperty("status").GetString());
        var saved = await _admin.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct);
        Assert.Equal(fingerprint, Assert.Single(saved!.TrustedKeys).Fingerprint);
    }

    [Fact]
    public async Task Open_ReportsAWrongPassword_WithoutEchoingIt()
    {
        sql.SkipIfUnavailable();
        var host = await CreateHostAsync();
        await OpenAsync(host);

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/mssql/sessions", Request(host, credential: host.Credentials[1].Id), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("authentication_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_RefusesWindowsLogins()
    {
        sql.SkipIfUnavailable();
        var host = await CreateHostAsync();

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/mssql/sessions", Request(host, credential: host.Credentials[2].Id), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQL logins", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_RunsBatchesInOrder_WithResultsMessagesAndErrorsAtTheirLines()
    {
        sql.SkipIfUnavailable();
        var session = await OpenAsync(await CreateHostAsync());

        var result = await QueryAsync(session, """
            PRINT 'starting';
            SELECT 1 AS [One], N'two' AS [Two], CAST(NULL AS int) AS [Nothing], CAST(1 AS bit), 0x0A0B, CAST('2024-05-06 07:08:09.123' AS datetime) AS [When], CAST(12.50 AS decimal(9,2)) AS [Price];
            GO
            SELECT name FROM sys.databases WHERE database_id <= 2 ORDER BY database_id;
            SELECT * FROM dbo.DoesNotExist;
            GO 2
            SELECT 'after the error' AS [Still];
            """);

        var output = result.GetProperty("output").EnumerateArray().ToList();
        Assert.Equal(["message", "results", "rows", "results", "rows", "error", "results", "rows", "error", "results", "rows"], output.Select(o => o.GetProperty("kind").GetString()));
        Assert.Equal("starting", output[0].GetProperty("text").GetString());

        var first = output[1];
        Assert.Equal(["One", "Two", "Nothing", "(No column name)", "(No column name)", "When", "Price"], first.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.True(first.GetProperty("columns")[0].GetProperty("numeric").GetBoolean());
        Assert.Equal(["1", "two", null, "1", "0x0A0B", "2024-05-06 07:08:09.123", "12.50"], first.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("(1 row affected)", output[2].GetProperty("text").GetString());
        Assert.Equal(["master", "tempdb"], output[3].GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()));

        // The error is reported at its line in the script (line 5), each time the batch runs, and the script carries on.
        var error = output[5];
        Assert.Equal(208, error.GetProperty("number").GetInt32());
        Assert.Equal(16, error.GetProperty("severity").GetInt32());
        Assert.Equal(5, error.GetProperty("line").GetInt32());
        Assert.Contains("DoesNotExist", error.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("after the error", output[9].GetProperty("rows")[0][0].GetString());
        Assert.True(result.GetProperty("hasErrors").GetBoolean());
    }

    [Fact]
    public async Task Query_KeepsTheSessionsState_BetweenQueries()
    {
        sql.SkipIfUnavailable();
        var session = await OpenAsync(await CreateHostAsync());

        var use = await QueryAsync(session, "USE tempdb; CREATE TABLE #notes (id int); INSERT #notes VALUES (1), (2);");
        Assert.Equal("tempdb", use.GetProperty("database").GetString());
        Assert.Equal("(2 rows affected)", use.GetProperty("output").EnumerateArray().Last().GetProperty("text").GetString());

        var again = await QueryAsync(session, "SELECT COUNT(*) FROM #notes; SELECT DB_NAME();", lineOffset: 10);
        var output = again.GetProperty("output").EnumerateArray().Where(o => o.GetProperty("kind").GetString() == "results").ToList();
        Assert.Equal("2", output[0].GetProperty("rows")[0][0].GetString());
        Assert.Equal("tempdb", output[1].GetProperty("rows")[0][0].GetString());

        var change = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/database", new { name = "msdb" }, Ct);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal("msdb", (await QueryAsync(session, "SELECT 1")).GetProperty("database").GetString());

        // varchar is read in the server's code page (the API runs with invariant globalization).
        var text = await QueryAsync(session, "SELECT CAST('caf' + CHAR(233) AS varchar(10)) COLLATE Latin1_General_CI_AS");
        Assert.Equal("café", text.GetProperty("output")[0].GetProperty("rows")[0][0].GetString());

        // A selection's errors are reported at the editor's lines.
        var selection = await QueryAsync(session, "SELECT 1/0;", lineOffset: 10);
        Assert.Equal(11, selection.GetProperty("output").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "error").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Query_CanBeCancelled_AndTheSessionStaysUsable()
    {
        sql.SkipIfUnavailable();
        var session = await OpenAsync(await CreateHostAsync());

        var running = _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/query", new { sql = "WAITFOR DELAY '00:01:00';" }, Ct);
        await Task.Delay(1000, Ct);

        // One query at a time per session.
        var busy = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/query", new { sql = "SELECT 1" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/cancel", null, Ct)).StatusCode);
        var cancelled = await (await running).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(cancelled.GetProperty("cancelled").GetBoolean());
        Assert.Equal("1", (await QueryAsync(session, "SELECT 1")).GetProperty("output")[0].GetProperty("rows")[0][0].GetString());
    }

    [Fact]
    public async Task Sessions_BelongToTheUserWhoOpenedThem_AndEndWhenClosed()
    {
        sql.SkipIfUnavailable();
        var session = await OpenAsync(await CreateHostAsync());

        await _factory.CreateUserAsync("mallory", "mallory@nkeeper.test", "Mallory-Passw0rd!");
        using var mallory = _factory.CreateClient();
        (await mallory.LoginAsync("mallory", "Mallory-Passw0rd!", Ct)).EnsureSuccessStatusCode();
        var stolen = await mallory.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/query", new { sql = "SELECT 1" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/mssql/sessions/{session}", null, Ct)).StatusCode);
        var ended = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/query", new { sql = "SELECT 1" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, ended.StatusCode);
        Assert.Contains("session_not_found", await ended.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explorer_BrowsesDatabasesTablesAndColumns_LikeSsms()
    {
        sql.SkipIfUnavailable();
        var database = $"nk_{Guid.NewGuid():N}"[..20];
        await sql.ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken: Ct);
        await sql.ExecuteAsync(
            """
            CREATE TABLE dbo.Customers (Id int IDENTITY(1,1) CONSTRAINT PK_Customers PRIMARY KEY, Name nvarchar(50) NOT NULL, Notes varchar(max) NULL);
            CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);
            EXEC ('CREATE VIEW dbo.CustomerNames AS SELECT Name FROM dbo.Customers');
            EXEC ('CREATE PROCEDURE dbo.FindCustomer @name nvarchar(50), @count int OUTPUT AS SELECT @count = COUNT(*) FROM dbo.Customers WHERE Name = @name');
            """,
            database,
            Ct);
        var session = await OpenAsync(await CreateHostAsync());

        var server = await ExploreAsync(session, new { kind = "server" });
        Assert.Equal(["Databases", "Security", "Management"], server.Select(n => n.GetProperty("label").GetString()));

        var databases = await ExploreAsync(session, server[0]);
        Assert.Equal("System Databases", databases[0].GetProperty("label").GetString());
        var db = databases.Single(n => n.GetProperty("label").GetString() == database);
        Assert.Equal(["master", "model", "msdb", "tempdb"], (await ExploreAsync(session, databases[0])).Select(n => n.GetProperty("label").GetString()));

        var folders = await ExploreAsync(session, db);
        Assert.Equal(["Tables", "Views", "Synonyms", "Programmability", "Security"], folders.Select(n => n.GetProperty("label").GetString()));
        var tables = await ExploreAsync(session, folders[0]);
        var customers = tables.Single(n => n.GetProperty("label").GetString() == "dbo.Customers");
        var tableFolders = await ExploreAsync(session, customers);
        Assert.Equal(["Columns", "Keys", "Indexes", "Triggers"], tableFolders.Select(n => n.GetProperty("label").GetString()));

        var columns = await ExploreAsync(session, tableFolders[0]);
        Assert.Equal(
            ["Id: PK, int, not null, identity", "Name: nvarchar(50), not null", "Notes: varchar(max), null"],
            columns.Select(n => $"{n.GetProperty("label").GetString()}: {n.GetProperty("detail").GetString()}"));
        Assert.Equal(["PK_Customers"], (await ExploreAsync(session, tableFolders[1])).Select(n => n.GetProperty("label").GetString()));
        Assert.Equal(
            ["PK_Customers: Primary key, Clustered", "IX_Customers_Name: Non-Unique, Non-Clustered"],
            (await ExploreAsync(session, tableFolders[2])).Select(n => $"{n.GetProperty("label").GetString()}: {n.GetProperty("detail").GetString()}"));

        var programmability = await ExploreAsync(session, folders[3]);
        var procedure = Assert.Single(await ExploreAsync(session, programmability[0]));
        Assert.Equal(
            ["@name: nvarchar(50), Input, No default", "@count: int, Input/Output, No default"],
            (await ExploreAsync(session, procedure)).Select(n => $"{n.GetProperty("label").GetString()}: {n.GetProperty("detail").GetString()}"));

        var management = await ExploreAsync(session, server[2]);
        Assert.Equal("Blocking Manager", Assert.Single(management).GetProperty("label").GetString());

        // Scripts, as SSMS writes them.
        var select = await ScriptAsync(session, customers, "selectTop");
        Assert.Contains("SELECT TOP (1000) [Id]\n      ,[Name]\n      ,[Notes]", select, StringComparison.Ordinal);
        Assert.Contains($"FROM [{database}].[dbo].[Customers]", select, StringComparison.Ordinal);
        var create = await ScriptAsync(session, customers, "create");
        Assert.Contains("CREATE TABLE [dbo].[Customers](\n\t[Id] int IDENTITY(1,1) NOT NULL,\n\t[Name] nvarchar(50) NOT NULL,", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED", create, StringComparison.Ordinal);
        var view = (await ExploreAsync(session, folders[1])).Single(n => n.GetProperty("label").GetString() == "dbo.CustomerNames");
        Assert.Contains("CREATE VIEW dbo.CustomerNames AS SELECT Name FROM dbo.Customers", await ScriptAsync(session, view, "create"), StringComparison.Ordinal);
        Assert.Contains("EXECUTE @RC = [dbo].[FindCustomer] @name,\n   @count OUTPUT", await ScriptAsync(session, procedure, "execute"), StringComparison.Ordinal);

        // Odd names are quoted, never pasted in.
        var odd = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/explore", new { node = new { kind = "tables", database = "x]; DROP DATABASE master; --" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        Assert.Contains("sql_error", await odd.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockingManager_ShowsChainsUnderTheirLeadBlocker_AndKillsIt()
    {
        sql.SkipIfUnavailable();
        var database = $"nk_{Guid.NewGuid():N}"[..20];
        await sql.ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken: Ct);
        await sql.ExecuteAsync("CREATE TABLE dbo.Accounts (Id int PRIMARY KEY, Balance int); INSERT dbo.Accounts VALUES (1, 100);", database, Ct);
        var session = await OpenAsync(await CreateHostAsync());

        // A lead blocker: an open transaction, sleeping. Two sessions wait on it, and one more waits on the first of those.
        await using var lead = await sql.OpenAsync(database, Ct);
        int leadId;
        await using (var spid = new Microsoft.Data.SqlClient.SqlCommand("SELECT @@SPID", lead))
        {
            leadId = (short)(await spid.ExecuteScalarAsync(Ct))!;
        }

        await using (var begin = new Microsoft.Data.SqlClient.SqlCommand("BEGIN TRAN; UPDATE dbo.Accounts SET Balance = 0 WHERE Id = 1;", lead))
        {
            await begin.ExecuteNonQueryAsync(Ct);
        }

        var waiters = new List<(Microsoft.Data.SqlClient.SqlConnection Connection, Task Work)>();
        async Task WaitAsync(string sqlText)
        {
            var connection = await sql.OpenAsync(database, Ct);
            var command = new Microsoft.Data.SqlClient.SqlCommand(sqlText, connection) { CommandTimeout = 120 };
            waiters.Add((connection, command.ExecuteNonQueryAsync(Ct)));
            await Task.Delay(500, Ct);
        }

        await WaitAsync("BEGIN TRAN; UPDATE dbo.Accounts SET Balance = 1 WHERE Id = 1; COMMIT;");
        await WaitAsync("SELECT * FROM dbo.Accounts WITH (UPDLOCK) WHERE Id = 1;");
        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/mssql/sessions/{session}/blocking", Ct);
        Assert.True(report.GetProperty("canSeeAllSessions").GetBoolean());
        var chain = Assert.Single(report.GetProperty("leads").EnumerateArray());
        Assert.Equal(leadId, chain.GetProperty("sessionId").GetInt32());
        Assert.Equal("sleeping", chain.GetProperty("status").GetString());
        Assert.Equal("sa", chain.GetProperty("login").GetString());
        Assert.Equal(database, chain.GetProperty("database").GetString());
        Assert.Equal(1, chain.GetProperty("openTransactions").GetInt32());
        Assert.Equal(JsonValueKind.Null, chain.GetProperty("blockedBy").ValueKind);
        Assert.Contains("UPDATE dbo.Accounts SET Balance = 0", chain.GetProperty("sql").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, chain.GetProperty("blockedVolume").GetInt32());
        Assert.InRange(chain.GetProperty("blocked").GetInt32(), 1, 2);
        var blocked = chain.GetProperty("blockedSessions")[0];
        Assert.False(string.IsNullOrEmpty(blocked.GetProperty("waitType").GetString()));
        Assert.Equal(2, report.GetProperty("blockedCount").GetInt32());

        var kill = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/kill", new { sessionId = leadId }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, kill.StatusCode);
        await Task.WhenAll(waiters.Select(w => w.Work)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        foreach (var (connection, _) in waiters)
        {
            await connection.DisposeAsync();
        }

        var after = await _admin.GetFromJsonAsync<JsonElement>($"/api/mssql/sessions/{session}/blocking", Ct);
        Assert.Empty(after.GetProperty("leads").EnumerateArray());
        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=mssql", Ct);
        Assert.Contains($"KILL {leadId}", audit.GetRawText(), StringComparison.Ordinal);
    }

    private async Task<HostResponse> CreateHostAsync()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sql01",
            ipAddress = SqlServerFixture.Address,
            services = new object[] { new { protocol = "Ssms", port = sql.Port } },
            credentials = new object[]
            {
                new { username = SqlServerFixture.UserName, password = SqlServerFixture.Password },
                new { username = SqlServerFixture.UserName, password = "wrong-password" },
                new { username = "dba", domain = "CORP", password = "irrelevant" },
            },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private static object Request(HostResponse host, Guid? credential = null, string? trust = null) =>
        new { hostId = host.Id, serviceIndex = 0, credentialId = credential ?? host.Credentials[0].Id, trustFingerprint = trust };

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Trusts the server's certificate and opens a session; returns its ID.</summary>
    private async Task<string> OpenAsync(HostResponse host)
    {
        var first = await PostAsync("/api/mssql/sessions", Request(host));
        if (first.GetProperty("status").GetString() == "ready")
        {
            return first.GetProperty("sessionId").GetString()!;
        }

        var fingerprint = first.GetProperty("certificate").GetProperty("fingerprint").GetString();
        return (await PostAsync("/api/mssql/sessions", Request(host, trust: fingerprint))).GetProperty("sessionId").GetString()!;
    }

    private Task<JsonElement> QueryAsync(string session, string sqlText, int lineOffset = 0) =>
        PostAsync($"/api/mssql/sessions/{session}/query", new { sql = sqlText, lineOffset });

    private async Task<List<JsonElement>> ExploreAsync(string session, object node) =>
        [.. (await PostAsync($"/api/mssql/sessions/{session}/explore", new { node })).EnumerateArray()];

    private async Task<string> ScriptAsync(string session, JsonElement node, string action) =>
        (await PostAsync($"/api/mssql/sessions/{session}/script", new { node, action })).GetProperty("sql").GetString()!;
}
