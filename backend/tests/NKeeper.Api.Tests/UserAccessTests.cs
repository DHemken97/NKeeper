using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Contracts.Users;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>User management and per-user host access, against real PostgreSQL.</summary>
public sealed class UserAccessTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private const string BobPassword = "Bob-Passw0rd-123";
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
    public async Task User_SeesOnlyGrantedHosts_WithTheGrantedLevel()
    {
        var dc1 = await CreateHostAsync("dc1-web", ("site", "DC1"));
        await CreateHostAsync("dc2-web", ("site", "DC2"));
        var lab = await CreateHostAsync("lab");
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        await GrantAsync(bob.Id, new { tagName = "SITE", tagValue = "dc1", level = "Use" }, new { hostId = lab.Id, level = "Manage" });

        var (client, _) = await SignInAsync("bob", BobPassword);
        var hosts = await ListAsync(client);

        Assert.Equal(["dc1-web", "lab"], hosts.Select(h => h.Name));
        Assert.Equal([false, true], hosts.Select(h => h.CanManage));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/hosts/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.True((await ListAsync(_admin)).All(h => h.CanManage));
        Assert.Equal(dc1.Id, (await client.GetFromJsonAsync<HostResponse>($"/api/hosts/{dc1.Id}", Json, Ct))!.Id);
    }

    [Fact]
    public async Task GroupGrant_FollowsTags_IncludingHostsTaggedLater()
    {
        await CreateHostAsync("a", ("site", "DC1"));
        await CreateHostAsync("b", ("site", "DC2"));
        var c = await CreateHostAsync("c");
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        await GrantAsync(bob.Id, new { tagName = "site", level = "Use" });
        var (client, _) = await SignInAsync("bob", BobPassword);
        Assert.Equal(["a", "b"], (await ListAsync(client)).Select(h => h.Name));

        var update = await _admin.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{c.Id}",
            new { name = "c", ipAddress = "10.0.0.9", tags = new[] { new { name = "site", value = "DC3" } }, version = c.Version }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.Equal(["a", "b", "c"], (await ListAsync(client)).Select(h => h.Name));
    }

    [Fact]
    public async Task UseLevel_CannotChangeHosts_ManageLevelCan_OnlyAdminsCreate()
    {
        var used = await CreateHostAsync("used");
        var managed = await CreateHostAsync("managed");
        var hidden = await CreateHostAsync("hidden");
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        await GrantAsync(bob.Id, new { hostId = used.Id, level = "Use" }, new { hostId = managed.Id, level = "Manage" });
        var (client, _) = await SignInAsync("bob", BobPassword);

        var editUsed = await client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{used.Id}", new { name = "x", ipAddress = "10.0.0.1", version = used.Version }, Ct);
        var editManaged = await client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{managed.Id}", new { name = "renamed", ipAddress = "10.0.0.1", version = managed.Version }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, editUsed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editManaged.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{used.Id}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{used.Id}/trusted-keys", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{hidden.Id}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{hidden.Id}", new { name = "x", ipAddress = "10.0.0.1", version = hidden.Version }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "new", ipAddress = "10.0.0.2" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{managed.Id}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task RemoteSession_ToAHostWithoutAccess_IsRefusedLikeAMissingHost()
    {
        var hidden = await CreateHostAsync("hidden", credential: true);
        await CreateUserAsync("bob", mustChangePassword: false);
        var (_, cookies) = await SignInAsync("bob", BobPassword);

        await using var terminal = await TerminalClient.ConnectAsync(_factory, cookies, Ct);
        await terminal.SendJsonAsync(new { type = "start", hostId = hidden.Id, serviceIndex = 0, credentialId = hidden.Credentials[0].Id, cols = 80, rows = 24 }, Ct);

        Assert.Equal(SessionErrorCodes.HostNotFound, (await terminal.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OnlyAdministrators_ManageUsers()
    {
        await CreateUserAsync("bob", mustChangePassword: false);
        var (client, _) = await SignInAsync("bob", BobPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendJsonAsync(HttpMethod.Post, "/api/users",
            new { userName = "eve", email = "eve@nkeeper.test", role = "Administrator", password = "Eve-Passw0rd-123" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task NewUser_MustChangeTheAssignedPassword_BeforeUsingNKeeper()
    {
        await CreateUserAsync("bob", mustChangePassword: true);
        var (client, _) = await SignInAsync("bob", BobPassword);

        var blocked = await client.GetAsync("/api/hosts", Ct);
        using (var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("password_change_required", problem.RootElement.GetProperty("code").GetString());
        }

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());

        var wrong = await client.SendJsonAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = "nope", newPassword = "Bob-New-Passw0rd" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("currentPassword", await wrong.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var changed = await client.SendJsonAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = "Bob-New-Passw0rd" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/hosts", Ct)).StatusCode);
    }

    [Fact]
    public async Task ChangingOwnPassword_KeepsThisSession_AndEndsTheOthers()
    {
        await CreateUserAsync("bob", mustChangePassword: false);
        var (laptop, _) = await SignInAsync("bob", BobPassword);
        var (phone, _) = await SignInAsync("bob", BobPassword);

        var changed = await laptop.SendJsonAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = "Bob-New-Passw0rd" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/hosts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/hosts", Ct)).StatusCode);
    }

    [Fact]
    public async Task DisablingAUser_EndsTheirSessionsImmediately_AndBlocksSignIn()
    {
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        var (client, _) = await SignInAsync("bob", BobPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/hosts", Ct)).StatusCode);

        var disable = await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bob.Id}", new { email = "bob@nkeeper.test", role = "User", isDisabled = true }, Ct);

        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hosts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync("bob", BobPassword)).StatusCode);
    }

    [Fact]
    public async Task ResettingAPassword_EndsSessions_AndRequiresAChange()
    {
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        var (client, _) = await SignInAsync("bob", BobPassword);

        var reset = await _admin.SendJsonAsync(HttpMethod.Post, $"/api/users/{bob.Id}/password", new { password = "Temp-Passw0rd-999" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hosts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync("bob", BobPassword)).StatusCode);
        var (fresh, _) = await SignInAsync("bob", "Temp-Passw0rd-999");
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync("/api/hosts", Ct)).StatusCode);
    }

    [Fact]
    public async Task PromotingAUser_EndsTheirSessions_SoTheNewRoleApplies()
    {
        await CreateHostAsync("anything");
        var bob = await CreateUserAsync("bob", mustChangePassword: false);
        var (client, _) = await SignInAsync("bob", BobPassword);
        Assert.Empty(await ListAsync(client));

        await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bob.Id}", new { email = "bob@nkeeper.test", role = "Administrator" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hosts", Ct)).StatusCode);
        var (again, _) = await SignInAsync("bob", BobPassword);
        Assert.Single(await ListAsync(again));
    }

    [Fact]
    public async Task Administrators_CannotLockThemselvesOut()
    {
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{me}", new { email = NKeeperApiFactory.AdminEmail, role = "User" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{me}", new { email = NKeeperApiFactory.AdminEmail, role = "Administrator", isDisabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Delete, $"/api/users/{me}", null, Ct)).StatusCode);

        // A second administrator may demote the first; the last one standing stays protected.
        var alice = await CreateUserAsync("alice", mustChangePassword: false, role: "Administrator");
        var (aliceClient, _) = await SignInAsync("alice", BobPassword);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendJsonAsync(HttpMethod.Put, $"/api/users/{me}", new { email = NKeeperApiFactory.AdminEmail, role = "User" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendJsonAsync(HttpMethod.Put, $"/api/users/{alice.Id}", new { email = "alice@nkeeper.test", role = "User" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("""{"userName":"bob","email":"bob@nkeeper.test","role":"User","password":"short"}""", "password")]
    [InlineData("""{"userName":"admin","email":"other@nkeeper.test","role":"User","password":"Long-Enough-Passw0rd"}""", "userName")]
    [InlineData("""{"userName":"bad name","email":"x@nkeeper.test","role":"User","password":"Long-Enough-Passw0rd"}""", "userName")]
    [InlineData("""{"userName":"bob","role":"User","email":"not-an-email","password":"Long-Enough-Passw0rd"}""", "email")]
    public async Task CreateUser_WithInvalidInput_ReturnsFieldError(string body, string field)
    {
        var token = await _admin.GetAntiforgeryTokenAsync(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);

        var response = await _admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains(field, problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Grants_RoundTrip_WithHostNames_AndDuplicatesCollapse()
    {
        var host = await CreateHostAsync("db01");
        var bob = await CreateUserAsync("bob", mustChangePassword: false);

        var user = await GrantAsync(bob.Id,
            new { hostId = host.Id, level = "Use" },
            new { hostId = host.Id, level = "Manage" },
            new { tagName = "env", tagValue = "prod", level = "Use" });

        Assert.Equal(2, user.Grants.Count);
        Assert.Contains(user.Grants, g => g.HostName == "db01" && g.Level == NKeeper.Infrastructure.Access.HostAccessLevel.Manage);
        Assert.Contains(user.Grants, g => g is { TagName: "env", TagValue: "prod" });
        Assert.Equal(2, (await _admin.GetFromJsonAsync<List<UserSummaryResponse>>("/api/users", Json, Ct))!.Single(u => u.UserName == "bob").GrantCount);
    }

    private async Task<HostResponse> CreateHostAsync(string name, (string Name, string Value)? tag = null, bool credential = false)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = "10.0.0.1",
            tags = tag is { } t ? new[] { new { name = t.Name, value = t.Value } } : [],
            services = new[] { new { protocol = "Ssh" } },
            credentials = credential ? new[] { new { username = "root", password = "secret" } } : [],
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<UserResponse> CreateUserAsync(string userName, bool mustChangePassword, string role = "User")
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName, email = $"{userName}@nkeeper.test", role, password = BobPassword, mustChangePassword }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json, Ct))!;
    }

    private async Task<UserResponse> GrantAsync(Guid userId, params object[] grants)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{userId}/grants", new { grants }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json, Ct))!;
    }

    private static async Task<List<HostResponse>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!;

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync(string userName, string password)
    {
        var cookies = new CookieContainer();
        var client = _factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        (await client.LoginAsync(userName, password, Ct)).EnsureSuccessStatusCode();
        return (client, cookies);
    }

    private async Task<HttpResponseMessage> TryLoginAsync(string userName, string password)
    {
        using var client = _factory.CreateClient();
        return await client.LoginAsync(userName, password, Ct);
    }
}
