using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Api.Tests;

/// <summary>
/// The sample plugin (tests/NKeeper.SamplePlugin), signed and packaged here, loaded the way a server
/// loads any plugin: verified, extracted, in its own load context, with its routes, settings, host
/// fields and browser files.
/// </summary>
public sealed class PluginLoadingTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("nkeeper-plugin-test-");
    private readonly PluginSigningKey _key = PluginSigningKey.Generate();
    private readonly List<NKeeperApiFactory> _factories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        var content = Path.Combine(_work.FullName, "content");
        var backend = Path.Combine(content, "backend");
        Directory.CreateDirectory(backend);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "sample-plugin")))
        {
            File.Copy(file, Path.Combine(backend, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(Path.Combine(content, "frontend"));
        File.WriteAllText(Path.Combine(content, "frontend", "index.js"), "window.sampleLoaded = true");
        Directory.CreateDirectory(Path.Combine(_work.FullName, "plugins"));
        PluginPackage.Write(
            new PluginManifest
            {
                Id = "sample",
                Name = "Sample",
                Version = "1.0.0",
                SdkVersion = "1.0",
                Backend = new PluginBackend { Assembly = "backend/NKeeper.SamplePlugin.dll" },
                Frontend = new PluginFrontend { Entry = "frontend/index.js" },
            },
            content,
            _key,
            Path.Combine(_work.FullName, "plugins", "sample.nkplugin"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        try
        {
            _work.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows keeps a loaded plugin's assembly locked until the test process ends.
        }
    }

    [Fact]
    public async Task ASignedPackage_IsLoadedInItsOwnContext_UnderItsRoute_WithItsConfiguration()
    {
        var admin = await StartAsync();

        var hello = await admin.GetFromJsonAsync<JsonElement>("/api/plugins/sample/hello", Ct);

        Assert.Equal("Hi", hello.GetProperty("message").GetString());
        Assert.Equal("plugin:sample", hello.GetProperty("loadContext").GetString());
    }

    [Fact]
    public async Task TheBrowserPart_IsListed_AndOnlyItsSignedFilesAreServed()
    {
        var admin = await StartAsync();

        var plugins = await admin.GetFromJsonAsync<JsonElement>("/api/plugins", Ct);
        var sample = Assert.Single(plugins.EnumerateArray());
        var script = sample.GetProperty("script").GetString()!;
        Assert.StartsWith("/api/plugins/sample/ui/index.js?v=", script, StringComparison.Ordinal);

        Assert.Equal("window.sampleLoaded = true", await admin.GetStringAsync(script, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/sample/ui/../manifest.json", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/sample/ui/other.js", Ct)).StatusCode);
    }

    [Fact]
    public async Task APackageSignedByAnotherKey_IsRefused_AndAdministratorsSeeWhy()
    {
        var admin = await StartAsync(trustedKey: PluginSigningKey.Generate().PublicKey);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/sample/hello", Ct)).StatusCode);
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/plugins", Ct);
        Assert.DoesNotContain(overview.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "sample");
        var rejected = Assert.Single(overview.GetProperty("rejected").EnumerateArray());
        Assert.Contains("doesn't trust", rejected.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATurnedOffPlugin_IsntLoaded()
    {
        var admin = await StartAsync(extra: new Dictionary<string, string> { ["Plugins:Disabled:0"] = "sample" });

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/sample/hello", Ct)).StatusCode);
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/plugins", Ct);
        var plugin = Assert.Single(overview.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "sample");
        Assert.Equal("disabled", plugin.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AdministratorsSeeTheSigner()
    {
        var admin = await StartAsync();

        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/plugins", Ct);
        var plugin = Assert.Single(overview.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "sample");
        Assert.Equal("loaded", plugin.GetProperty("status").GetString());
        Assert.Equal(_key.PublicKey.KeyId, plugin.GetProperty("signer").GetString());
        Assert.Contains(_key.PublicKey.KeyId, overview.GetProperty("trustedKeys").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Settings_KeepSecretsEncrypted()
    {
        var admin = await StartAsync();

        var saved = await admin.SendJsonAsync(HttpMethod.Put, "/api/plugins/sample/settings", new { colour = "blue", token = "s3cret-token" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/plugins/sample/settings", Ct);
        Assert.Equal("blue", settings.GetProperty("colour").GetString());
        Assert.True(settings.GetProperty("hasToken").GetBoolean());
        Assert.Equal("s3cret-token", settings.GetProperty("token").GetString());

        await using var scope = _factories[^1].Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().PluginSettings.SingleAsync(s => s.PluginId == "sample" && s.Name == "token", Ct);
        Assert.Null(row.Value);
        Assert.DoesNotContain("s3cret-token", System.Text.Encoding.UTF8.GetString(row.ProtectedValue!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostFields_AreValidated_ReturnedWithHosts_AndEditable()
    {
        var admin = await StartAsync();
        var created = await admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "web01", ipAddress = "10.0.0.5" }, Ct);
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = host.GetProperty("id").GetGuid();

        var set = await admin.SendJsonAsync(HttpMethod.Post, $"/api/plugins/sample/tickets/{id}?ticket=OPS-42", new { }, Ct);
        var updated = Assert.Single((await set.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray());
        Assert.Equal("OPS-42", updated.GetProperty("plugins").GetProperty("sample").GetProperty("ticket").GetString());
        var tickets = await admin.GetFromJsonAsync<Dictionary<Guid, string>>("/api/plugins/sample/tickets", Ct);
        Assert.Equal("OPS-42", tickets![id]);

        var invalid = await admin.SendJsonAsync(HttpMethod.Post, $"/api/plugins/sample/tickets/{id}?ticket=nope", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("A ticket is like ABC-123.", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The host editor sends plugin values with the host; ones left out are kept.
        var edited = await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", new
        {
            name = "web01",
            ipAddress = "10.0.0.5",
            version = updated.GetProperty("version").GetUInt32(),
            plugins = new Dictionary<string, object> { ["sample"] = new Dictionary<string, string?> { ["ticket"] = " OPS-43 " } },
        }, Ct);
        edited.EnsureSuccessStatusCode();
        var afterEdit = await edited.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("OPS-43", afterEdit.GetProperty("plugins").GetProperty("sample").GetProperty("ticket").GetString());

        var unknown = await admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{id}", new
        {
            name = "web01",
            ipAddress = "10.0.0.5",
            version = afterEdit.GetProperty("version").GetUInt32(),
            plugins = new Dictionary<string, object> { ["missing"] = new Dictionary<string, string?> { ["x"] = "1" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("plugins.missing.x", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<HttpClient> StartAsync(PluginPublicKey? trustedKey = null, Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Plugins:Directories:0"] = Path.Combine(_work.FullName, "plugins"),
            ["Plugins:TrustedKeys:0"] = (trustedKey ?? _key.PublicKey).ToBase64(),
            ["Plugins:CacheDirectory"] = Path.Combine(_work.FullName, "cache"),
            ["Plugins:Settings:sample:Greeting"] = "Hi",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), settings: settings);
        _factories.Add(factory);
        return await factory.CreateSignedInClientAsync(Ct);
    }
}
