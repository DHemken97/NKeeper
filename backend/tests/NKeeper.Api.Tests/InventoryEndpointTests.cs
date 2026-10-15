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

/// <summary>Host inventory, credential library, groups and tags against real PostgreSQL.</summary>
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

    [Theory]
    [InlineData("/api/hosts")]
    [InlineData("/api/credentials")]
    [InlineData("/api/groups")]
    [InlineData("/api/tags")]
    public async Task InventoryEndpoints_RequireAuthentication(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateHost_StoresNormalizedFieldsAndRelations()
    {
        var group = await CreateGroupAsync("Datacenter A");
        var credential = await CreateCredentialAsync("Domain admin", password: "S3cret!");

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "  sql01  ",
            hostname = "sql01.corp.example.com",
            ipAddress = "10.0.0.15",
            macAddress = "aa-bb-cc-dd-ee-0f",
            description = "Primary SQL Server",
            groupId = group.Id,
            tags = new[] { "production", "SQL", "Production" },
            services = new object[]
            {
                new { protocol = "Rdp" },
                new { protocol = "Ssms", port = 1433, label = "Default instance" },
            },
            credentialIds = new[] { credential.Id },
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var host = await ReadAsync<HostResponse>(response);
        Assert.Equal("sql01", host.Name);
        Assert.Equal("AA:BB:CC:DD:EE:0F", host.MacAddress);
        Assert.Equal("Datacenter A", host.Group!.Name);
        Assert.Equal(["production", "SQL"], host.Tags);
        Assert.Equal([ServiceProtocol.Rdp, ServiceProtocol.Ssms], host.Services.Select(s => s.Protocol));
        Assert.Equal(1433, host.Services[1].Port);
        Assert.Equal(credential.Id, Assert.Single(host.Credentials).Id);
        Assert.Equal(response.Headers.Location!.AbsolutePath, $"/api/hosts/{host.Id}");

        var fetched = await ReadAsync<HostResponse>(await _client.GetAsync($"/api/hosts/{host.Id}", Ct));
        Assert.Equal(host, fetched with { Tags = host.Tags, Services = host.Services, Credentials = host.Credentials });
    }

    [Theory]
    [InlineData("""{"name":"x"}""", "hostname")]
    [InlineData("""{"name":"x","ipAddress":"10.1"}""", "ipAddress")]
    [InlineData("""{"name":"x","hostname":"bad host!"}""", "hostname")]
    [InlineData("""{"name":"x","hostname":"h","macAddress":"zz:zz"}""", "macAddress")]
    [InlineData("""{"name":"x","hostname":"h","services":[{"protocol":"Ssh","port":70000}]}""", "services[0].port")]
    [InlineData("""{"name":"x","hostname":"h","services":[{}]}""", "services[0].protocol")]
    [InlineData("""{"name":"","hostname":"h"}""", "name")]
    [InlineData("""{"name":"x","hostname":"h","groupId":"6f1c0d4e-0000-4000-8000-000000000001"}""", "groupId")]
    [InlineData("""{"name":"x","hostname":"h","credentialIds":["6f1c0d4e-0000-4000-8000-000000000001"]}""", "credentialIds")]
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
    public async Task ListHosts_SearchesAcrossFieldsWithEveryTermRequired()
    {
        var group = await CreateGroupAsync("Branch Office");
        await CreateHostAsync("web01", ip: "192.168.10.5", tags: ["nginx"]);
        await CreateHostAsync("web02", hostname: "web02.example.com", tags: ["iis"], groupId: group.Id);
        await CreateHostAsync("db01", hostname: "db01.example.com", description: "50% capacity");

        Assert.Equal(["web01", "web02"], await SearchNamesAsync("search=web"));
        Assert.Equal(["web01"], await SearchNamesAsync("search=192.168.10"));
        Assert.Equal(["web02"], await SearchNamesAsync("search=IIS"));
        Assert.Equal(["web02"], await SearchNamesAsync("search=branch"));
        Assert.Equal(["web02"], await SearchNamesAsync("search=web%20example.com"));
        Assert.Equal(["db01"], await SearchNamesAsync("search=50%25")); // "%" is matched literally
        Assert.Empty(await SearchNamesAsync("search=web%20db"));
    }

    [Fact]
    public async Task ListHosts_FiltersByGroupTagAndService()
    {
        var group = await CreateGroupAsync("Linux");
        await CreateHostAsync("a", hostname: "a", groupId: group.Id, tags: ["prod"], services: [ServiceProtocol.Ssh]);
        await CreateHostAsync("b", hostname: "b", tags: ["prod"], services: [ServiceProtocol.Rdp]);
        await CreateHostAsync("c", hostname: "c", services: [ServiceProtocol.Ssh, ServiceProtocol.Vnc]);

        Assert.Equal(["a"], await SearchNamesAsync($"groupId={group.Id}"));
        Assert.Equal(["b", "c"], await SearchNamesAsync("ungrouped=true"));
        Assert.Equal(["a", "b"], await SearchNamesAsync("tag=PROD"));
        Assert.Equal(["a", "c"], await SearchNamesAsync("protocol=ssh"));
        Assert.Equal(["a"], await SearchNamesAsync("protocol=Ssh&tag=prod"));
    }

    [Fact]
    public async Task ListHosts_SortsAndPages()
    {
        // Mixed case: sorting must be case-insensitive ("Bravo" between "alpha" and "charlie").
        await CreateHostAsync("Bravo", hostname: "z.example.com");
        await CreateHostAsync("alpha", hostname: "m.example.com");
        await CreateHostAsync("charlie", ip: "10.0.0.1");

        Assert.Equal(["charlie", "Bravo", "alpha"], await SearchNamesAsync("sort=name&direction=desc"));
        Assert.Equal(["charlie", "alpha", "Bravo"], await SearchNamesAsync("sort=address"));

        var page = await ReadAsync<PagedResponse<HostSummaryResponse>>(await _client.GetAsync("/api/hosts?pageSize=2&page=2", Ct));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal("charlie", Assert.Single(page.Items).Name);

        var invalid = await _client.GetAsync("/api/hosts?sort=nonsense", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task UpdateHost_ReplacesValuesAndRejectsStaleVersions()
    {
        var host = await CreateHostAsync("app01", hostname: "app01", tags: ["old-tag"], services: [ServiceProtocol.Ssh]);
        var update = new { name = "app01", hostname = "app01", tags = new[] { "new-tag" }, services = new[] { new { protocol = "Http", port = 8080 } }, version = host.Version };

        var first = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", update, Ct);
        var stale = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", update, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await ReadAsync<HostResponse>(first);
        Assert.Equal(["new-tag"], updated.Tags);
        Assert.Equal(ServiceProtocol.Http, Assert.Single(updated.Services).Protocol);
        Assert.NotEqual(host.Version, updated.Version);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(Ct));
        Assert.Equal("concurrency_conflict", problem.RootElement.GetProperty("code").GetString());

        // Tags no host uses any more are removed.
        var tags = await ReadAsync<List<TagResponse>>(await _client.GetAsync("/api/tags", Ct));
        Assert.Equal([new TagResponse("new-tag", 1)], tags);
    }

    [Fact]
    public async Task DeleteHost_RemovesItAndReturns404Afterwards()
    {
        var host = await CreateHostAsync("temp", hostname: "temp", tags: ["short-lived"]);

        var delete = await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}", null, Ct);
        var get = await _client.GetAsync($"/api/hosts/{host.Id}", Ct);
        var deleteAgain = await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        Assert.Empty(await ReadAsync<List<TagResponse>>(await _client.GetAsync("/api/tags", Ct)));
    }

    [Fact]
    public async Task Credentials_SecretsAreEncryptedAndNeverReturned()
    {
        const string password = "Pa55word-that-must-not-leak";
        const string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----";

        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/credentials",
            new { name = "Linux root", username = "root", password, privateKey }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN OPENSSH", body, StringComparison.Ordinal);
        var credential = JsonSerializer.Deserialize<CredentialResponse>(body, Json)!;
        Assert.True(credential.HasPassword);
        Assert.True(credential.HasPrivateKey);

        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Credentials.AsNoTracking().SingleAsync(Ct);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(stored.ProtectedPassword!), StringComparison.Ordinal);
        Assert.Equal(password, scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(stored.ProtectedPassword!));
    }

    [Fact]
    public async Task UpdateCredential_KeepsOrClearsSecretsExplicitly()
    {
        var credential = await CreateCredentialAsync("svc", password: "first-password");

        var keep = await ReadAsync<CredentialResponse>(await _client.SendJsonAsync(HttpMethod.Put, $"/api/credentials/{credential.Id}",
            new { name = "svc renamed", username = "svc", version = credential.Version }, Ct));
        var cleared = await ReadAsync<CredentialResponse>(await _client.SendJsonAsync(HttpMethod.Put, $"/api/credentials/{credential.Id}",
            new { name = "svc renamed", clearPassword = true, version = keep.Version }, Ct));

        Assert.True(keep.HasPassword);
        Assert.Equal("svc renamed", keep.Name);
        Assert.False(cleared.HasPassword);
    }

    [Fact]
    public async Task DeleteCredential_DetachesItFromHosts()
    {
        var credential = await CreateCredentialAsync("shared");
        var host = await CreateHostAsync("h1", hostname: "h1", credentialIds: [credential.Id]);
        Assert.Equal(1, (await ReadAsync<List<CredentialResponse>>(await _client.GetAsync("/api/credentials", Ct))).Single().HostCount);

        var delete = await _client.SendJsonAsync(HttpMethod.Delete, $"/api/credentials/{credential.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await ReadAsync<HostResponse>(await _client.GetAsync($"/api/hosts/{host.Id}", Ct))).Credentials);
    }

    [Fact]
    public async Task Groups_RejectDuplicateNamesAndDeletingUngroupsHosts()
    {
        var group = await CreateGroupAsync("Servers");
        var host = await CreateHostAsync("h1", hostname: "h1", groupId: group.Id);

        var duplicate = await _client.SendJsonAsync(HttpMethod.Post, "/api/groups", new { name = "SERVERS" }, Ct);
        var delete = await _client.SendJsonAsync(HttpMethod.Delete, $"/api/groups/{group.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Null((await ReadAsync<HostResponse>(await _client.GetAsync($"/api/hosts/{host.Id}", Ct))).Group);
    }

    private async Task<List<string>> SearchNamesAsync(string queryString)
    {
        var response = await _client.GetAsync($"/api/hosts?{queryString}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ReadAsync<PagedResponse<HostSummaryResponse>>(response)).Items.Select(h => h.Name)];
    }

    private async Task<HostResponse> CreateHostAsync(
        string name,
        string? hostname = null,
        string? ip = null,
        string? description = null,
        Guid? groupId = null,
        string[]? tags = null,
        ServiceProtocol[]? services = null,
        Guid[]? credentialIds = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            hostname,
            ipAddress = ip,
            description,
            groupId,
            tags = tags ?? [],
            services = (services ?? []).Select(p => new { protocol = p.ToString() }),
            credentialIds = credentialIds ?? [],
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<HostResponse>(response);
    }

    private async Task<GroupResponse> CreateGroupAsync(string name)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/groups", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<GroupResponse>(response);
    }

    private async Task<CredentialResponse> CreateCredentialAsync(string name, string? password = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/credentials", new { name, password }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CredentialResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
}
