using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Combining duplicate hosts: the kept host gets what the others have, and what pointed at them.</summary>
public sealed class HostMergeTests(PostgresServerFixture postgres) : IAsyncLifetime
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

    private async Task<HostResponse> CreateAsync(object body)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    [Fact]
    public async Task Duplicates_are_combined_into_the_kept_host_and_deleted()
    {
        var kept = await CreateAsync(new
        {
            name = "app01",
            ipAddress = "10.0.0.5",
            tags = new[] { new { name = "env", value = "prod" } },
            services = new[] { new { protocol = "Ssh", port = 22 } },
            credentials = new object[] { new { username = "root", password = "kept-secret" } },
        });
        var duplicate = await CreateAsync(new
        {
            name = "APP01 (old)",
            ipAddress = "10.0.0.5",
            macAddress = "00:11:22:33:44:55",
            serialNumber = "ABC123",
            tags = new[] { new { name = "ENV", value = "PROD" }, new { name = "role", value = "web" } },
            services = new object[] { new { protocol = "Ssh", port = 22 }, new { protocol = "Http", port = 8080 } },
            credentials = new object[]
            {
                new { username = "ROOT", password = "old-secret" },
                new { username = "deploy", password = "d", serviceIndexes = new[] { 1 } },
            },
        });
        var vm = await CreateAsync(new { name = "vm1", ipAddress = "10.0.0.20", isVirtualMachine = true, hypervisorId = duplicate.Id });
        var behind = await CreateAsync(new
        {
            name = "behind",
            ipAddress = "192.168.9.9",
            jumpHostId = duplicate.Id,
            jumpCredentialId = duplicate.Credentials.Single(c => c.Username == "ROOT").Id,
        });

        // A user given the duplicate (only) keeps access to the kept host.
        const string password = "Plain-User-Passw0rd";
        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", password);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            var plainId = await db.Users.Where(u => u.UserName == "plain").Select(u => u.Id).SingleAsync(Ct);
            db.HostAccessGrants.Add(new HostAccessGrant { UserId = plainId, HostId = duplicate.Id, Level = HostAccessLevel.Use, CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var response = await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{kept.Id}/merge", new { sourceIds = new[] { duplicate.Id } }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var merged = (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;

        // The kept host's details stay; the ones it lacked come from the duplicate.
        Assert.Equal(("app01", "10.0.0.5", "00:11:22:33:44:55", "ABC123"), (merged.Name, merged.IpAddress, merged.MacAddress, merged.SerialNumber));
        Assert.Equal(["env=prod", "role=web"], merged.Tags.Select(t => $"{t.Name}={t.Value}").Order());
        Assert.Equal([(ServiceProtocol.Ssh, 22), (ServiceProtocol.Http, 8080)], merged.Services.Select(s => (s.Protocol, s.Port ?? 0)));

        // root is the same account: the kept host's password stays. deploy comes along, still for the web service only.
        Assert.Equal(["root", "deploy"], merged.Credentials.Select(c => c.Username));
        var deploy = merged.Credentials.Single(c => c.Username == "deploy");
        Assert.Equal(merged.Services.Single(s => s.Protocol == ServiceProtocol.Http).Id, (Guid?)Assert.Single(deploy.ServiceIds!));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
            var root = await db.Set<HostCredential>().SingleAsync(c => c.HostId == kept.Id && c.Username == "root", Ct);
            Assert.Equal("kept-secret", protector.Unprotect(root.ProtectedPassword!));
            Assert.False(await db.Hosts.AnyAsync(h => h.Id == duplicate.Id, Ct));
            Assert.Equal(kept.Id, (await db.HostAccessGrants.SingleAsync(Ct)).HostId);
        }

        // What pointed at the duplicate points at the kept host.
        var hosts = (await _client.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!;
        Assert.Equal(kept.Id, hosts.Single(h => h.Id == vm.Id).HypervisorId);
        var reached = hosts.Single(h => h.Id == behind.Id);
        Assert.Equal((kept.Id, merged.Credentials.Single(c => c.Username == "root").Id), (reached.JumpHostId!.Value, reached.JumpCredentialId!.Value));

        var audit = await _client.GetStringAsync("/api/audit/events?limit=50", Ct);
        Assert.Contains("APP01 (old) → app01", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merging_needs_other_hosts_that_can_be_managed()
    {
        var a = await CreateAsync(new { name = "a", ipAddress = "10.0.0.1" });
        var b = await CreateAsync(new { name = "b", ipAddress = "10.0.0.1" });

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{a.Id}/merge", new { sourceIds = Array.Empty<Guid>() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{a.Id}/merge", new { sourceIds = new[] { a.Id } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{a.Id}/merge", new { sourceIds = new[] { Guid.NewGuid() } }, Ct)).StatusCode);

        // A user who can only use the hosts can't combine them.
        const string password = "Plain-User-Passw0rd";
        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", password);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            var plainId = await db.Users.Where(u => u.UserName == "plain").Select(u => u.Id).SingleAsync(Ct);
            db.HostAccessGrants.AddRange(
                new HostAccessGrant { UserId = plainId, HostId = a.Id, Level = HostAccessLevel.Manage, CreatedAtUtc = DateTimeOffset.UtcNow },
                new HostAccessGrant { UserId = plainId, HostId = b.Id, Level = HostAccessLevel.Use, CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var plain = _factory.CreateDefaultClient(new CookieContainerHandler());
        (await plain.LoginAsync("plain", password, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{a.Id}/merge", new { sourceIds = new[] { b.Id } }, Ct)).StatusCode);
        Assert.Equal(2, (await _client.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!.Count);
    }
}
