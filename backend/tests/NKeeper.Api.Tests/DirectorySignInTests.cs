using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Active Directory sign-in (against a stand-in directory) and keeping the domain login for RDP.</summary>
public sealed class DirectorySignInTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private const string Password = "Domain-Passw0rd!";

    private readonly FakeDirectory _directory = new FakeDirectory()
        .Add("jdoe", Password, email: "john.doe@example.test")
        .Add("bob", Password);

    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), settings: FakeDirectory.Settings(), directory: _directory);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData(@"CORP\jdoe")]
    [InlineData("jdoe")]
    [InlineData("JDoe@corp.test")]
    public async Task FirstSignIn_CreatesTheAccount_AndKeepsTheDomainLoginEncrypted(string identifier)
    {
        using var client = NewClient();
        var response = await client.LoginAsync(identifier, Password, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("jdoe", me.GetProperty("userName").GetString());
        Assert.Equal("john.doe@example.test", me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("isDirectoryAccount").GetBoolean());
        Assert.Equal(@"CORP\jdoe", me.GetProperty("signInAccount").GetString());
        Assert.Empty(me.GetProperty("roles").EnumerateArray());

        // The session (not the cookie) holds it, so /me finds it again.
        var again = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.Equal(@"CORP\jdoe", again.GetProperty("signInAccount").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == "jdoe", Ct);
        Assert.True(user.IsDirectoryAccount);
        Assert.Null(user.PasswordHash);
        var stored = Assert.Single(await db.AuthSessions.Where(s => s.UserId == user.Id).ToListAsync(Ct)).ProtectedSignInPassword;
        Assert.NotNull(stored);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectoryAccount_CanThenSignInWithItsEmail()
    {
        using var first = NewClient();
        (await first.LoginAsync("jdoe", Password, Ct)).EnsureSuccessStatusCode();

        using var second = NewClient();
        Assert.Equal(HttpStatusCode.OK, (await second.LoginAsync("john.doe@example.test", Password, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(@"CORP\jdoe", "wrong-password")]
    [InlineData(@"OTHER\jdoe", Password)]
    [InlineData("nobody", Password)]
    public async Task WrongPasswordOrAccount_FailsLikeAnyOtherSignIn(string identifier, string password)
    {
        using var client = NewClient();
        var response = await client.LoginAsync(identifier, password, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_credentials", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LocalAccount_KeepsItsOwnPassword_AndIsNotTakenOverByTheDirectory()
    {
        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Local-Passw0rd");

        using var client = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync(@"CORP\bob", Password, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("bob", Password, Ct)).StatusCode);

        var local = await client.LoginAsync("bob", "Bob-Local-Passw0rd", Ct);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var me = await local.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(me.GetProperty("isDirectoryAccount").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("signInAccount").ValueKind);
    }

    [Fact]
    public async Task SignOut_DeletesTheStoredLogin()
    {
        using var client = NewClient();
        (await client.LoginAsync("jdoe", Password, Ct)).EnsureSuccessStatusCode();
        (await client.LogoutAsync(Ct)).EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        Assert.False(await db.AuthSessions.AnyAsync(s => s.ProtectedSignInPassword != null, Ct));
    }

    [Fact]
    public async Task AdministratorsGroup_GrantsAndRemovesTheRoleAtSignIn()
    {
        _directory.Add("alice", Password, isAdministrator: true);
        using var client = NewClient();
        var asAdmin = await (await client.LoginAsync("alice", Password, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["Administrator"], asAdmin.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        _directory.Add("alice", Password, isAdministrator: false);
        using var later = NewClient();
        var asUser = await (await later.LoginAsync("alice", Password, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(asUser.GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task DirectoryPasswords_AreNotChangedOrResetInNKeeper()
    {
        using var client = NewClient();
        var me = await (await client.LoginAsync("jdoe", Password, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var change = await client.SendJsonAsync(
            HttpMethod.Post, "/api/account/password", new { currentPassword = Password, newPassword = "Another-Passw0rd!" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var reset = await admin.SendJsonAsync(
            HttpMethod.Post, $"/api/users/{me.GetProperty("id").GetString()}/password", new { password = "Another-Passw0rd!", mustChangePassword = false }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NewClient().LoginAsync("jdoe", Password, Ct)).StatusCode);
    }

    [Fact]
    public async Task PassThroughOff_KeepsNoPassword()
    {
        await using var factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct), settings: FakeDirectory.Settings(passCredentials: false), directory: _directory);
        using var client = factory.CreateDefaultClient(new CookieContainerHandler());
        var me = await (await client.LoginAsync("jdoe", Password, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(me.GetProperty("isDirectoryAccount").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("signInAccount").ValueKind);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        Assert.False(await db.AuthSessions.AnyAsync(s => s.ProtectedSignInPassword != null, Ct));
    }

    [Fact]
    public async Task LocalSignIn_HasNoDomainLoginToPassOn()
    {
        var (client, cookies) = await SignedInCookies.SignInAsync(_factory, Ct);
        using (client)
        {
            var host = await CreateHostAsync(client, "Rdp");
            await using var session = await TerminalClient.ConnectAsync(_factory, cookies, Ct, protocol: "rdp");
            await session.SendJsonAsync(new { type = "start", hostId = host, serviceIndex = 0, useSignInCredentials = true, width = 800, height = 600, dpi = 96 }, Ct);

            var error = await session.ExpectAsync("error", Ct);
            Assert.Equal(SessionErrorCodes.SignInCredentialsUnavailable, error.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task DomainLogin_IsOnlyPassedOnToRdp()
    {
        _directory.Add("carol", Password, isAdministrator: true);
        var cookies = new System.Net.CookieContainer();
        using var client = _factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        (await client.LoginAsync("carol", Password, Ct)).EnsureSuccessStatusCode();
        var host = await CreateHostAsync(client, "Ssh");

        await using var session = await TerminalClient.ConnectAsync(_factory, cookies, Ct, protocol: "ssh");
        await session.SendJsonAsync(new { type = "start", hostId = host, serviceIndex = 0, useSignInCredentials = true, cols = 80, rows = 24 }, Ct);

        var error = await session.ExpectAsync("error", Ct);
        Assert.Equal(SessionErrorCodes.BadRequest, error.GetProperty("code").GetString());
    }

    private HttpClient NewClient() => _factory.CreateDefaultClient(new CookieContainerHandler());

    private static async Task<Guid> CreateHostAsync(HttpClient client, string protocol)
    {
        var response = await client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"host-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { protocol, port = (int?)1 } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
