using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Settings ▸ Limits: how much a host may hold, set by administrators and enforced on every save.</summary>
public sealed class InventoryLimitsTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Host(string name, int services, int tags = 0) => new
    {
        name,
        ipAddress = "10.0.0.1",
        services = Enumerable.Range(0, services).Select(i => new { protocol = "Ssh", port = 2200 + i }).ToArray(),
        tags = Enumerable.Range(0, tags).Select(i => new { name = $"tag{i}", value = "x" }).ToArray(),
    };

    private static object Limits(int services, int credentials = 100, int tags = 50, int scanUsers = 10) =>
        new { maxServicesPerHost = services, maxCredentialsPerHost = credentials, maxTagsPerHost = tags, scanUsersPreselectedUpTo = scanUsers };

    [Fact]
    public async Task Defaults_allow_more_than_the_old_twenty_services()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);

        var limits = await admin.GetFromJsonAsync<JsonElement>("/api/settings/limits", Ct);
        Assert.Equal(100, limits.GetProperty("maxServicesPerHost").GetInt32());
        Assert.Equal(10, limits.GetProperty("scanUsersPreselectedUpTo").GetInt32());
        Assert.Equal(HttpStatusCode.Created, (await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", Host("many", 40), Ct)).StatusCode);
    }

    [Fact]
    public async Task Administrators_set_the_limits_and_every_save_follows_them()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/limits", Limits(services: 2, tags: 1), Ct)).EnsureSuccessStatusCode();

        var tooMany = await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", Host("three", 3), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("at most 2 services", await tooMany.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", Host("tags", 1, tags: 2), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", Host("two", 2), Ct)).StatusCode);

        // Out of range is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/limits", Limits(services: 0), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/limits", Limits(services: 1001), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_host_over_a_lowered_limit_can_still_be_saved_but_not_gain_more()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var created = await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", Host("five", 5), Ct);
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = host.GetProperty("id").GetString();
        (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/limits", Limits(services: 2), Ct)).EnsureSuccessStatusCode();

        object Update(string name, int services) => new
        {
            name,
            ipAddress = "10.0.0.1",
            services = Enumerable.Range(0, services).Select(i => new { protocol = "Ssh", port = 2200 + i }).ToArray(),
            version = host.GetProperty("version").GetUInt32(),
        };

        var renamed = await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", Update("five-renamed", 5), Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        host = await renamed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", Update("six", 6), Ct)).StatusCode);
    }

    [Fact]
    public async Task Everyone_reads_the_limits_but_only_administrators_change_them()
    {
        const string password = "Plain-User-Passw0rd";
        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", password);
        using var plain = _factory.CreateDefaultClient(new CookieContainerHandler());
        (await plain.LoginAsync("plain", password, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/api/settings/limits", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.SendJsonAsync(HttpMethod.Put, "/api/admin/limits", Limits(services: 500), Ct)).StatusCode);
    }
}
