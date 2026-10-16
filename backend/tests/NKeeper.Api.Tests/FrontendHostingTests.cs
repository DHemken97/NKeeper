using System.Net;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Without nginx (e.g. under IIS), a frontend deployed in wwwroot is served by the API itself,
/// with nginx's security and cache headers; the API's own routes are unaffected.
/// </summary>
public sealed class FrontendHostingTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("nkeeper-wwwroot-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><div id=\"root\"></div>");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-abc123.js"), "console.log('nkeeper')");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_webRoot, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ServesTheFrontend_WithSecurityHeaders_AndLeavesTheApiAlone()
    {
        await using var factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct), settings: new Dictionary<string, string> { ["webroot"] = _webRoot });
        var client = factory.CreateClient();

        var route = await client.GetAsync("/hosts/some-client-route", Ct);
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        Assert.Contains("id=\"root\"", await route.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("no-cache", route.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", route.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        var asset = await client.GetAsync("/assets/app-abc123.js", Ct);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("console.log('nkeeper')", await asset.Content.ReadAsStringAsync(Ct));
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        // Missing files are not answered with index.html (anonymous requests get the API's usual 401).
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/assets/missing.js", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/no-such-endpoint", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
    }

    [Fact]
    public async Task WithoutAFrontend_ClientRoutesAreNotServed()
    {
        await using var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/hosts/some-client-route", Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }
}
