using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Hosts with their owned tags, services and credentials, against real PostgreSQL.</summary>
public sealed class InventoryEndpointTests(PostgresServerFixture postgres) : IAsyncLifetime
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
    public async Task HostEndpoints_RequireAuthentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/hosts", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateHost_StoresNormalizedFieldsAndOwnedItems()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "  sql01  ",
            hostname = "sql01.corp.example.com",
            ipAddress = "10.0.0.15",
            macAddress = "aa-bb-cc-dd-ee-0f",
            description = "Primary SQL Server",
            tags = new object[]
            {
                new { name = "site", value = "Datacenter A" },
                new { name = "env", value = " production " },
                new { name = "ENV", value = "PRODUCTION" },
                new { name = "backup" },
            },
            services = new object[]
            {
                new { protocol = "Rdp" },
                new { protocol = "Https", port = 8443, label = "Reporting" },
            },
            credentials = new object[]
            {
                new { label = "Local admin", username = "administrator", password = "secret-1" },
                new { username = "sa", domain = "CORP" },
            },
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var host = await ReadAsync<HostResponse>(response);
        Assert.Equal("sql01", host.Name);
        Assert.Equal("AA:BB:CC:DD:EE:0F", host.MacAddress);
        Assert.Equal(
            [new HostTagResponse("backup", null), new HostTagResponse("env", "production"), new HostTagResponse("site", "Datacenter A")],
            host.Tags);
        Assert.Equal([ServiceProtocol.Rdp, ServiceProtocol.Https], host.Services.Select(s => s.Protocol));
        Assert.Equal(["Local admin", null], host.Credentials.Select(c => c.Label));
        Assert.Equal([true, false], host.Credentials.Select(c => c.HasPassword));
        Assert.Equal("CORP", host.Credentials[1].Domain);
        Assert.Equal(response.Headers.Location!.AbsolutePath, $"/api/hosts/{host.Id}");
    }

    [Theory]
    [InlineData("""{"name":"x"}""", "hostname")]
    [InlineData("""{"name":"x","ipAddress":"10.1"}""", "ipAddress")]
    [InlineData("""{"name":"x","hostname":"bad host!"}""", "hostname")]
    [InlineData("""{"name":"x","hostname":"h","macAddress":"zz:zz"}""", "macAddress")]
    [InlineData("""{"name":"x","hostname":"h","services":[{"protocol":"Ssh","port":70000}]}""", "services[0].port")]
    [InlineData("""{"name":"x","hostname":"h","services":[{}]}""", "services[0].protocol")]
    [InlineData("""{"name":"","hostname":"h"}""", "name")]
    [InlineData("""{"name":"x","hostname":"h","tags":[{"name":""}]}""", "tags[0].name")]
    [InlineData("""{"name":"x","hostname":"h","tags":[{"name":"a:b"}]}""", "tags[0].name")]
    [InlineData("""{"name":"x","hostname":"h","credentials":[{"password":"p"}]}""", "credentials[0].username")]
    [InlineData("""{"name":"x","hostname":"h","credentials":[{"id":"6f1c0d4e-0000-4000-8000-000000000001","username":"u"}]}""", "credentials[0].id")]
    public async Task CreateHost_WithInvalidInput_ReturnsFieldError(string body, string field)
    {
        var token = await _client.GetAntiforgeryTokenAsync(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/hosts") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains(field, errors);
    }

    [Fact]
    public async Task ListHosts_ReturnsWholeInventorySortedCaseInsensitively()
    {
        await CreateHostAsync("charlie", ip: "10.0.0.3");
        await CreateHostAsync("Bravo", hostname: "bravo", tags: [("env", "prod")]);
        await CreateHostAsync("alpha", hostname: "alpha");

        var hosts = await ReadAsync<List<HostResponse>>(await _client.GetAsync("/api/hosts", Ct));

        Assert.Equal(["alpha", "Bravo", "charlie"], hosts.Select(h => h.Name));
        Assert.Equal([new HostTagResponse("env", "prod")], hosts[1].Tags);
    }

    [Fact]
    public async Task UpdateHost_ReplacesTagsAndServicesAndRejectsStaleVersions()
    {
        var host = await CreateHostAsync("app01", hostname: "app01", tags: [("env", "staging"), ("role", "web")]);
        var update = new
        {
            name = "app01",
            hostname = "app01",
            tags = new object[] { new { name = "env", value = "production" }, new { name = "role", value = "web" } },
            services = new[] { new { protocol = "Http", port = 8080 } },
            version = host.Version,
        };

        var first = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", update, Ct);
        var stale = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", update, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await ReadAsync<HostResponse>(first);
        Assert.Equal([new HostTagResponse("env", "production"), new HostTagResponse("role", "web")], updated.Tags);
        Assert.Equal(ServiceProtocol.Http, Assert.Single(updated.Services).Protocol);
        Assert.NotEqual(host.Version, updated.Version);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(Ct));
        Assert.Equal("concurrency_conflict", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Credentials_SecretsAreEncryptedAndNeverReturned()
    {
        const string password = "Pa55word-that-must-not-leak";
        const string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----";

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "linux01",
            hostname = "linux01",
            credentials = new[] { new { username = "root", password, privateKey } },
        }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var list = await (await _client.GetAsync("/api/hosts", Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var json in new[] { body, list })
        {
            Assert.DoesNotContain(password, json, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN OPENSSH", json, StringComparison.Ordinal);
        }

        var credential = Assert.Single(JsonSerializer.Deserialize<HostResponse>(body, Json)!.Credentials);
        Assert.True(credential.HasPassword);
        Assert.True(credential.HasPrivateKey);

        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().HostCredentials.AsNoTracking().SingleAsync(Ct);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(stored.ProtectedPassword!), StringComparison.Ordinal);
        Assert.Equal(password, scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(stored.ProtectedPassword!));
    }

    [Fact]
    public async Task UpdateHost_KeepsClearsAndRemovesCredentialsById()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "db01",
            hostname = "db01",
            credentials = new object[]
            {
                new { username = "keep", password = "keep-password" },
                new { username = "clear", password = "clear-password" },
                new { username = "remove", password = "remove-password" },
            },
        }, Ct);
        var host = await ReadAsync<HostResponse>(response);
        var (keep, clear) = (host.Credentials[0], host.Credentials[1]);

        var updated = await ReadAsync<HostResponse>(await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = "db01",
            hostname = "db01",
            credentials = new object[]
            {
                new { username = "new" },
                new { id = clear.Id, username = "clear", clearPassword = true },
                new { id = keep.Id, username = "kept-renamed" },
            },
            version = host.Version,
        }, Ct));

        Assert.Equal(["new", "clear", "kept-renamed"], updated.Credentials.Select(c => c.Username));
        Assert.Equal([false, false, true], updated.Credentials.Select(c => c.HasPassword));
        Assert.Equal(keep.Id, updated.Credentials[2].Id);

        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().HostCredentials.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(3, stored.Count);
        Assert.Equal("keep-password", scope.ServiceProvider.GetRequiredService<CredentialProtector>()
            .Unprotect(stored.Single(c => c.Id == keep.Id).ProtectedPassword!));
    }

    [Fact]
    public async Task UpdateHost_CannotTakeOverAnotherHostsCredential()
    {
        var other = await ReadAsync<HostResponse>(await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts",
            new { name = "other", hostname = "other", credentials = new[] { new { username = "u", password = "p" } } }, Ct));
        var host = await CreateHostAsync("mine", hostname: "mine");

        var response = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = "mine",
            hostname = "mine",
            credentials = new[] { new { id = other.Credentials[0].Id, username = "u" } },
            version = host.Version,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CloningHost_CopiesCredentialSecretsOnTheServer()
    {
        var source = await ReadAsync<HostResponse>(await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web01",
            hostname = "web01",
            credentials = new object[]
            {
                new { username = "root", password = "root-secret", privateKey = "KEY-1" },
                new { username = "deploy", password = "deploy-secret" },
            },
        }, Ct));

        // The clone keeps root's secrets, replaces deploy's password, and adds a credential.
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web02",
            hostname = "web02",
            credentials = new object[]
            {
                new { username = "root", copyFromCredentialId = source.Credentials[0].Id },
                new { username = "deploy", copyFromCredentialId = source.Credentials[1].Id, password = "new-deploy-secret" },
                new { username = "extra", copyFromCredentialId = source.Credentials[1].Id, clearPassword = true },
            },
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = await ReadAsync<HostResponse>(response);
        Assert.Equal([true, true, false], clone.Credentials.Select(c => c.HasPassword));
        Assert.Equal([true, false, false], clone.Credentials.Select(c => c.HasPrivateKey));
        Assert.DoesNotContain(clone.Credentials, c => source.Credentials.Any(s => s.Id == c.Id));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        var stored = await db.HostCredentials.AsNoTracking().Where(c => c.HostId == clone.Id).OrderBy(c => c.Position).ToListAsync(Ct);
        Assert.Equal("root-secret", protector.Unprotect(stored[0].ProtectedPassword!));
        Assert.Equal("KEY-1", protector.Unprotect(stored[0].ProtectedPrivateKey!));
        Assert.Equal("new-deploy-secret", protector.Unprotect(stored[1].ProtectedPassword!));

        // The source is untouched.
        var original = await db.HostCredentials.AsNoTracking().Where(c => c.HostId == source.Id).OrderBy(c => c.Position).ToListAsync(Ct);
        Assert.Equal("deploy-secret", protector.Unprotect(original[1].ProtectedPassword!));
    }

    [Fact]
    public async Task CloningHost_RejectsUnknownSourceCredentials()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web02",
            hostname = "web02",
            credentials = new[] { new { username = "root", copyFromCredentialId = Guid.NewGuid() } },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("copyFromCredentialId", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteHost_RemovesOwnedItemsAndReturns404Afterwards()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "tmp",
            hostname = "tmp",
            tags = new[] { new { name = "env", value = "dev" } },
            services = new[] { new { protocol = "Ssh" } },
            credentials = new[] { new { username = "u", password = "p" } },
        }, Ct);
        var host = await ReadAsync<HostResponse>(response);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/hosts/{host.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}", null, Ct)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        Assert.Equal(0, await db.HostTags.CountAsync(Ct));
        Assert.Equal(0, await db.HostServices.CountAsync(Ct));
        Assert.Equal(0, await db.HostCredentials.CountAsync(Ct));
    }

    [Fact]
    public async Task UnknownProtocolInTheDatabase_ReadsAsOther_InsteadOfBreakingTheHostList()
    {
        // As left by a newer or feature build that shares the database.
        var host = await CreateHostAsync("future", ip: "10.0.0.9");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "HostServices" ("Id", "HostId", "Protocol", "Position") VALUES ({Guid.NewGuid()}, {host.Id}, 'TimeMachine', 0)""", Ct);
        }

        var response = await _client.GetAsync("/api/hosts", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = (await ReadAsync<List<HostResponse>>(response)).Single(h => h.Id == host.Id);
        Assert.Equal(ServiceProtocol.Other, Assert.Single(listed.Services).Protocol);
    }

    [Theory]
    [InlineData("Ssms")]
    [InlineData("Telnet")]
    [InlineData("WinRm")]
    [InlineData("Other")]
    public async Task ServicesNKeeperCantConnectWith_CantBeAdded(string protocol)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "legacy",
            ipAddress = "10.0.0.8",
            services = new object[] { new { protocol = "Ssh" }, new { protocol } },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("services[1].protocol", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredUnsupportedServices_StillLoad_AndGoWhenTheHostIsSavedWithoutThem()
    {
        var host = await CreateHostAsync("old", ip: "10.0.0.7");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "HostServices" ("Id", "HostId", "Protocol", "Position") VALUES ({Guid.NewGuid()}, {host.Id}, 'Telnet', 0)""", Ct);
        }

        var stored = await ReadAsync<HostResponse>(await _client.GetAsync($"/api/hosts/{host.Id}", Ct));
        Assert.Equal(ServiceProtocol.Telnet, Assert.Single(stored.Services).Protocol);

        var saved = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = "old",
            ipAddress = "10.0.0.7",
            services = new object[] { new { protocol = "Ssh" } },
            version = stored.Version,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(ServiceProtocol.Ssh, Assert.Single((await ReadAsync<HostResponse>(saved)).Services).Protocol);
    }

    private async Task<HostResponse> CreateHostAsync(
        string name,
        string? hostname = null,
        string? ip = null,
        (string Name, string? Value)[]? tags = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            hostname,
            ipAddress = ip,
            tags = (tags ?? []).Select(t => new { name = t.Name, value = t.Value }),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<HostResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
}
