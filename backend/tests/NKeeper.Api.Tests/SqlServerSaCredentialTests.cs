using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>An MS SQL service added to a host gets an "sa" credential of its own, with no password yet.</summary>
public sealed class SqlServerSaCredentialTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Service(string protocol, int port, string? instance = null) => new { protocol, port, instance };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The host's credentials as (username, has a password, the services' instances or ports they're used with).</summary>
    private static List<(string? User, bool Password, string Services)> Credentials(JsonElement host)
    {
        var services = host.GetProperty("services").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("instance").GetString() ?? s.GetProperty("port").ToString());
        return [.. host.GetProperty("credentials").EnumerateArray().Select(c => (
            c.GetProperty("username").GetString(),
            c.GetProperty("hasPassword").GetBoolean(),
            c.GetProperty("serviceIds").ValueKind == JsonValueKind.Null ? "all" : string.Join(",", c.GetProperty("serviceIds").EnumerateArray().Select(id => services[id.GetString()!]))))];
    }

    private static object Update(JsonElement host, object[] services, object[] credentials) => new
    {
        name = host.GetProperty("name").GetString(),
        hostname = host.GetProperty("hostname").GetString(),
        services,
        credentials,
        version = host.GetProperty("version").GetUInt32(),
    };

    [Fact]
    public async Task Each_new_sql_service_gets_its_own_sa_credential_once()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var host = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sql01",
            hostname = "sql01",
            services = new[] { Service("Ssh", 22), Service("Ssms", 1433, "A") },
            credentials = new[] { new { username = "root", password = "secret" } },
        }, Ct));
        Assert.Equal([("root", true, "all"), ("sa", false, "A")], Credentials(host));
        var id = host.GetProperty("id").GetString();

        // Adding a second instance gets a second sa; the first instance doesn't get another.
        object Existing(JsonElement h, int index) => new
        {
            id = h.GetProperty("credentials")[index].GetProperty("id").GetString(),
            username = h.GetProperty("credentials")[index].GetProperty("username").GetString(),
            serviceIndexes = h.GetProperty("credentials")[index].GetProperty("serviceIds").ValueKind == JsonValueKind.Null ? null : new[] { 1 },
        };
        host = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", Update(host,
            [Service("Ssh", 22), Service("Ssms", 1433, "A"), Service("Ssms", 1434, "B")],
            [Existing(host, 0), Existing(host, 1)]), Ct));
        Assert.Equal([("root", true, "all"), ("sa", false, "A"), ("sa", false, "B")], Credentials(host));

        // A removed sa credential isn't brought back while its service stays.
        host = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", Update(host,
            [Service("Ssh", 22), Service("Ssms", 1433, "A"), Service("Ssms", 1434, "B")],
            [Existing(host, 0)]), Ct));
        Assert.Equal([("root", true, "all")], Credentials(host));
    }

    [Fact]
    public async Task An_sa_credential_already_given_for_the_service_is_kept_as_it_is()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var host = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sql02",
            hostname = "sql02",
            services = new[] { Service("Ssms", 1433) },
            credentials = new[] { new { username = "SA", password = "typed" } },
        }, Ct));
        Assert.Equal([("SA", true, "all")], Credentials(host));
    }

    [Fact]
    public async Task Bulk_added_sql_services_get_one_too()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var host = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "web01", hostname = "web01", services = new[] { Service("Ssh", 22) } }, Ct));
        var updated = await ReadAsync(await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts/bulk", new
        {
            hostIds = new[] { host.GetProperty("id").GetString() },
            addServices = new[] { Service("Ssms", 1433) },
        }, Ct));
        Assert.Equal([("sa", false, "1433")], Credentials(updated[0]));
    }
}
