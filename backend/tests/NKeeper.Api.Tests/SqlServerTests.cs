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
    public async Task InternalScanScript_FindsSqlServerOnARealLinuxSqlServer()
    {
        sql.SkipIfUnavailable();

        // The scan's own script, run on the SQL Server container as its service account.
        var output = await sql.RunShellAsync(NKeeper.Api.Inventory.Scanning.InternalScan.Script);
        var findings = NKeeper.Api.Inventory.Scanning.InternalScan.Parse(output);

        var found = Assert.Single(findings.Ports, p => p.Protocol == ServiceProtocol.Ssms);
        Assert.Equal(1433, found.Port);
        Assert.Null(found.Instance);
        Assert.Equal("SQL Server (default instance)", found.Name);
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
    public async Task Closing_EndsTheConnectionOnTheServer_EvenWhileAQueryRuns()
    {
        sql.SkipIfUnavailable();
        var host = await CreateHostAsync();
        var trusted = await OpenAsync(host);
        await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/mssql/sessions/{trusted}", null, Ct);
        Assert.Equal(0, await NKeeperConnectionsAsync());

        var opened = await PostAsync("/api/mssql/sessions", Request(host));
        var session = opened.GetProperty("sessionId").GetString()!;
        var spid = opened.GetProperty("server").GetProperty("sessionId").GetInt32();
        Assert.True(await ConnectedAsync(spid));

        // Explorer requests use short connections of their own, closed straight after.
        await ExploreAsync(session, new { kind = "databases" });
        Assert.Equal(1, await NKeeperConnectionsAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/mssql/sessions/{session}", null, Ct)).StatusCode);
        Assert.False(await ConnectedAsync(spid));
        Assert.Equal(0, await NKeeperConnectionsAsync());

        // Closed in the middle of a query: the query stops and the connection goes too.
        var busy = await PostAsync("/api/mssql/sessions", Request(host));
        var busyId = busy.GetProperty("sessionId").GetString()!;
        var busySpid = busy.GetProperty("server").GetProperty("sessionId").GetInt32();
        var running = _admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{busyId}/query", new { sql = "WAITFOR DELAY '00:05:00';" }, Ct);
        await Task.Delay(1000, Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/mssql/sessions/{busyId}", null, Ct)).StatusCode);
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(await ConnectedAsync(busySpid));
        Assert.Equal(0, await NKeeperConnectionsAsync());
    }

    [Fact]
    public async Task ASessionWhoseBrowserWentAway_IsClosed_EvenMidQuery()
    {
        sql.SkipIfUnavailable();
        var clock = new ManualClock();
        await using var factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct), configureServices: services => services.AddSingleton<TimeProvider>(clock));
        var admin = await factory.CreateSignedInClientAsync(Ct);
        var host = await CreateHostAsync(admin);
        var opened = await OpenSessionAsync(admin, host);
        var session = opened.GetProperty("sessionId").GetString()!;
        var spid = opened.GetProperty("server").GetProperty("sessionId").GetInt32();
        var store = factory.Services.GetRequiredService<NKeeper.Api.Databases.DatabaseSessionStore>();

        // While the tab checks in, the session stays open, however long it's been.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/keepalive", null, Ct)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(2));
        store.RemoveExpired();
        Assert.True(await ConnectedAsync(spid));

        // The browser goes away in the middle of a long query: no more check-ins.
        var running = admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/query", new { sql = "WAITFOR DELAY '00:10:00';" }, Ct);
        await Task.Delay(1000, Ct);
        clock.Advance(TimeSpan.FromMinutes(4));
        store.RemoveExpired();

        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(await ConnectedAsync(spid));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendJsonAsync(HttpMethod.Post, $"/api/mssql/sessions/{session}/keepalive", null, Ct)).StatusCode);
    }

    /// <summary>Whether SQL Server still has NKeeper's connection with this SPID.</summary>
    private async Task<bool> ConnectedAsync(int spid)
    {
        // Closing is asynchronous on the server's side: give it a moment.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var connection = await sql.OpenAsync(cancellationToken: Ct);
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = @spid AND program_name = 'NKeeper'", connection);
            command.Parameters.AddWithValue("@spid", spid);
            if ((int)(await command.ExecuteScalarAsync(Ct))! == 0)
            {
                return false;
            }

            await Task.Delay(100, Ct);
        }

        return true;
    }

    private async Task<int> NKeeperConnectionsAsync()
    {
        await Task.Delay(300, Ct);
        await using var connection = await sql.OpenAsync(cancellationToken: Ct);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = 'NKeeper'", connection);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
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
        Assert.Equal(["Blocking Manager", "Orphaned Users"], management.Select(n => n.GetProperty("label").GetString()));

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
    public async Task OrphanedUsers_AreFound_AndFixedByMappingCreatingOrDropping()
    {
        sql.SkipIfUnavailable();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"nk_{suffix}";
        string Name(string name) => $"{name}_{suffix}";
        await sql.ExecuteAsync(
            $"""
            CREATE DATABASE [{database}];
            CREATE LOGIN [{Name("gone")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("moved")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("dropme")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("owner")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("spare")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            """,
            cancellationToken: Ct);
        await sql.ExecuteAsync(
            $"""
            CREATE USER [{Name("gone")}] FOR LOGIN [{Name("gone")}];
            CREATE USER [{Name("moved")}] FOR LOGIN [{Name("moved")}];
            CREATE USER [{Name("dropme")}] FOR LOGIN [{Name("dropme")}];
            CREATE USER [{Name("owner")}] FOR LOGIN [{Name("owner")}];
            CREATE USER [nologin_{suffix}] WITHOUT LOGIN;
            EXEC ('CREATE SCHEMA [sales_{suffix}] AUTHORIZATION [{Name("owner")}]');
            """,
            database,
            Ct);

        // As after restoring the database on another server: the logins are gone, or new ones have other SIDs.
        await sql.ExecuteAsync(
            $"""
            DROP LOGIN [{Name("gone")}];
            DROP LOGIN [{Name("dropme")}];
            DROP LOGIN [{Name("owner")}];
            DROP LOGIN [{Name("moved")}];
            CREATE LOGIN [{Name("moved")}] WITH PASSWORD = 'New-Passw0rd!', CHECK_POLICY = OFF;
            """,
            cancellationToken: Ct);
        var session = await OpenAsync(await CreateHostAsync());

        List<JsonElement> Orphans(JsonElement report) =>
            [.. report.GetProperty("users").EnumerateArray().Where(u => u.GetProperty("database").GetString() == database)];
        List<string?> Users(JsonElement report) => [.. Orphans(report).Select(u => u.GetProperty("user").GetString())];

        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/mssql/sessions/{session}/orphans", Ct);

        // Users without a login on purpose aren't orphans.
        Assert.Equal([Name("dropme"), Name("gone"), Name("moved"), Name("owner")], Users(report).Order());
        var orphans = Orphans(report);
        var moved = orphans.Single(u => u.GetProperty("user").GetString() == Name("moved"));
        Assert.Equal(Name("moved"), moved.GetProperty("matchingLogin").GetString());
        Assert.Equal("SQL user", moved.GetProperty("type").GetString());
        var gone = orphans.Single(u => u.GetProperty("user").GetString() == Name("gone"));
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("matchingLogin").ValueKind);
        Assert.StartsWith("0x", gone.GetProperty("sid").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            [$"sales_{suffix}"],
            orphans.Single(u => u.GetProperty("user").GetString() == Name("owner")).GetProperty("ownedSchemas").EnumerateArray().Select(v => v.GetString()));

        // The usual repair: map every user to the login of its own name.
        var matched = await PostAsync($"/api/mssql/sessions/{session}/orphans/fix-matched", new { });
        Assert.Contains("Mapped", matched.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Name("moved"), Users(matched.GetProperty("report")));

        // A new login with the user's SID, so other copies of the database map to it too.
        var created = await PostAsync(
            $"/api/mssql/sessions/{session}/orphans/fix", new { action = "create", database, user = Name("gone"), password = "Brand-New-Passw0rd!" });
        Assert.DoesNotContain(Name("gone"), Users(created.GetProperty("report")));
        await using (var check = await sql.OpenAsync(database, Ct))
        await using (var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM sys.database_principals u JOIN sys.server_principals p ON p.sid = u.sid WHERE u.name = @user AND p.name = @user", check))
        {
            command.Parameters.AddWithValue("@user", Name("gone"));
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync(Ct))!);
        }

        // A user that owns a schema can't be dropped; it can be mapped to another login.
        var refused = await _admin.SendJsonAsync(
            HttpMethod.Post, $"/api/mssql/sessions/{session}/orphans/fix", new { action = "drop", database, user = Name("owner") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains($"sales_{suffix}", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await PostAsync($"/api/mssql/sessions/{session}/orphans/fix", new { action = "map", database, user = Name("owner"), login = Name("spare") });

        var dropped = await PostAsync($"/api/mssql/sessions/{session}/orphans/fix", new { action = "drop", database, user = Name("dropme") });
        Assert.Empty(Orphans(dropped.GetProperty("report")));

        // Only users the check found can be changed through here.
        var other = await _admin.SendJsonAsync(
            HttpMethod.Post, $"/api/mssql/sessions/{session}/orphans/fix", new { action = "drop", database, user = $"nologin_{suffix}" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);

        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=mssql", Ct);
        Assert.Contains($"user {Name("dropme")} dropped", audit.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Brand-New-Passw0rd!", audit.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanedUsers_CanBeDroppedInBulk_EachSayingWhetherItWorked()
    {
        sql.SkipIfUnavailable();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"nk_{suffix}";
        string Name(string name) => $"{name}_{suffix}";
        await sql.ExecuteAsync(
            $"""
            CREATE DATABASE [{database}];
            CREATE LOGIN [{Name("a")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("b")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            CREATE LOGIN [{Name("keeper")}] WITH PASSWORD = 'Old-Passw0rd!', CHECK_POLICY = OFF;
            """,
            cancellationToken: Ct);
        await sql.ExecuteAsync(
            $"""
            CREATE USER [{Name("a")}] FOR LOGIN [{Name("a")}];
            CREATE USER [{Name("b")}] FOR LOGIN [{Name("b")}];
            CREATE USER [{Name("keeper")}] FOR LOGIN [{Name("keeper")}];
            EXEC ('CREATE SCHEMA [kept_{suffix}] AUTHORIZATION [{Name("keeper")}]');
            """,
            database,
            Ct);
        await sql.ExecuteAsync($"DROP LOGIN [{Name("a")}]; DROP LOGIN [{Name("b")}]; DROP LOGIN [{Name("keeper")}];", cancellationToken: Ct);
        var session = await OpenAsync(await CreateHostAsync());

        var bulk = await PostAsync($"/api/mssql/sessions/{session}/orphans/fix-many", new
        {
            fixes = new[] { Name("a"), Name("b"), Name("keeper") }.Select(user => new { action = "drop", database, user }),
        });

        Assert.Equal("Dropped 2 users. 1 couldn't be.", bulk.GetProperty("message").GetString());
        var results = bulk.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([true, true, false], results.Select(r => r.GetProperty("succeeded").GetBoolean()));
        Assert.Contains($"kept_{suffix}", results[2].GetProperty("message").GetString(), StringComparison.Ordinal);
        var left = bulk.GetProperty("report").GetProperty("users").EnumerateArray().Where(u => u.GetProperty("database").GetString() == database);
        Assert.Equal([Name("keeper")], left.Select(u => u.GetProperty("user").GetString()));

        // Creating logins needs a password each: one at a time.
        var create = await _admin.SendJsonAsync(
            HttpMethod.Post, $"/api/mssql/sessions/{session}/orphans/fix-many", new { fixes = new[] { new { action = "create", database, user = Name("keeper") } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
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

    [Fact]
    public async Task BlockingManager_ListsAParallelQueryBlockingItself()
    {
        sql.SkipIfUnavailable();
        var database = $"nk_{Guid.NewGuid():N}"[..20];
        await sql.ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken: Ct);
        await sql.ExecuteAsync("SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Id INTO dbo.Numbers FROM sys.all_columns a CROSS JOIN sys.all_columns b;", database, Ct);
        var session = await OpenAsync(await CreateHostAsync());

        await using var worker = await sql.OpenAsync(database, Ct);
        await using (var cpus = new Microsoft.Data.SqlClient.SqlCommand("SELECT cpu_count FROM sys.dm_os_sys_info", worker))
        {
            Assert.SkipWhen((int)(await cpus.ExecuteScalarAsync(Ct))! < 2, "A parallel query needs at least two CPUs.");
        }

        int workerId;
        await using (var spid = new Microsoft.Data.SqlClient.SqlCommand("SELECT @@SPID", worker))
        {
            workerId = (short)(await spid.ExecuteScalarAsync(Ct))!;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var query = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.Numbers a CROSS JOIN dbo.Numbers b WHERE a.Id + b.Id = 7 OPTION (MAXDOP 4, QUERYTRACEON 8649);", worker) { CommandTimeout = 120 };
        var running = query.ExecuteScalarAsync(stop.Token);

        JsonElement? lead = null;
        for (var attempt = 0; attempt < 40 && lead is null; attempt++)
        {
            await Task.Delay(250, Ct);
            var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/mssql/sessions/{session}/blocking", Ct);
            foreach (var candidate in report.GetProperty("leads").EnumerateArray())
            {
                if (candidate.GetProperty("sessionId").GetInt32() == workerId)
                {
                    lead = candidate;
                }
            }
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => running);

        Assert.NotNull(lead);
        Assert.True(lead.Value.GetProperty("selfBlocking").GetBoolean());
        Assert.Equal(0, lead.Value.GetProperty("blockedVolume").GetInt32());
        Assert.False(string.IsNullOrEmpty(lead.Value.GetProperty("waitType").GetString()));
        Assert.Contains("dbo.Numbers", lead.Value.GetProperty("sql").GetString(), StringComparison.Ordinal);
    }

    private static async Task<JsonElement> OpenSessionAsync(HttpClient client, HostResponse host)
    {
        async Task<JsonElement> Post(object body)
        {
            var response = await client.SendJsonAsync(HttpMethod.Post, "/api/mssql/sessions", body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        var first = await Post(Request(host));
        return first.GetProperty("status").GetString() == "ready"
            ? first
            : await Post(Request(host, trust: first.GetProperty("certificate").GetProperty("fingerprint").GetString()));
    }

    private async Task<HostResponse> CreateHostAsync(HttpClient? client = null)
    {
        var response = await (client ?? _admin).SendJsonAsync(HttpMethod.Post, "/api/hosts", new
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
