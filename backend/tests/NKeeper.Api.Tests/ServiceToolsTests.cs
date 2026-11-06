using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>Which tools a service's menus offer: stored with the host, and all of them unless told otherwise.</summary>
public sealed class ServiceToolsTests(PostgresServerFixture postgres) : IAsyncLifetime
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

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ServiceProtocol.Ssh, null, null)]
    [InlineData(ServiceProtocol.Ssh, "files,storage,journal,selinux", null)] // All of them is "all", so tools added later start on.
    [InlineData(ServiceProtocol.Ssh, "selinux,files", "files,selinux")] // In the tools' order.
    [InlineData(ServiceProtocol.Ssh, "", "")] // None is a choice, unlike null.
    [InlineData(ServiceProtocol.Rpc, "eventlog", null)]
    [InlineData(ServiceProtocol.Rpc, "", "")]
    [InlineData(ServiceProtocol.Rdp, "files", null)] // RDP has no tools.
    public void WhatsStored_IsOnlyAChoiceThatMatters(ServiceProtocol protocol, string? tools, string? expected)
    {
        var stored = ServiceTools.Normalize(protocol, tools?.Split(',', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expected, stored is null ? null : string.Join(',', stored));
    }

    [Fact]
    public async Task AHostWithoutAChoice_HasAllItsTools()
    {
        var host = await CreateAsync(new { protocol = "Ssh" });

        Assert.Null(host.Services[0].Tools);
    }

    [Fact]
    public async Task TheChoice_IsSaved_AndChanged_AndKeptWithItsService()
    {
        var host = await CreateAsync(new { protocol = "Rdp" }, new { protocol = "Ssh", tools = new[] { "files", "storage" } }, new { protocol = "Rpc", tools = Array.Empty<string>() });

        Assert.Equal(["files", "storage"], host.Services[1].Tools);
        Assert.Empty(host.Services[2].Tools!);
        Assert.Null(host.Services[0].Tools);

        var read = await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{host.Id}", Json, Ct);
        Assert.Equal(["files", "storage"], read!.Services[1].Tools);

        var updated = await UpdateAsync(host, new { protocol = "Ssh", tools = new[] { "files" } });
        Assert.Equal(["files"], Assert.Single(updated.Services).Tools);

        // Saving without a choice turns them all back on.
        var cleared = await UpdateAsync(updated, new { protocol = "Ssh" });
        Assert.Null(Assert.Single(cleared.Services).Tools);
    }

    [Theory]
    [InlineData("Ssh", "eventlog")] // Another service's tool.
    [InlineData("Rpc", "journal")]
    [InlineData("Ssh", "nonsense")]
    public async Task AToolTheServiceDoesntHave_IsRefused(string protocol, string tool)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"win-{Guid.NewGuid():N}",
            ipAddress = "10.0.0.30",
            services = new[] { new { protocol, tools = new[] { tool } } },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("isn't a tool of this service", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<HostResponse> CreateAsync(params object[] services)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = $"win-{Guid.NewGuid():N}", ipAddress = "10.0.0.30", services }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> UpdateAsync(HostResponse host, params object[] services)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = host.Name, ipAddress = host.IpAddress, services, version = host.Version }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }
}
