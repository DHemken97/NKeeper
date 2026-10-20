using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Single sign-on through OpenID Connect, against a stand-in identity provider.</summary>
public sealed class SingleSignOnTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private readonly FakeIdentityProvider _idp = new();
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _factory = await CreateFactoryAsync();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _idp.Dispose();
    }

    [Fact]
    public async Task SignInOptions_NameTheProvider()
    {
        using var client = NewClient(_factory);
        var options = await client.GetFromJsonAsync<JsonElement>("/api/auth/options", Ct);

        Assert.Equal("Contoso", options.GetProperty("singleSignOnName").GetString());
    }

    [Fact]
    public async Task FirstSignIn_CreatesTheAccount_AndReturnsToThePageAskedFor()
    {
        using var client = NewClient(_factory);
        var challenge = await client.GetAsync("/api/auth/sso/login?returnUrl=%2Freports%2Fhealth", Ct);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        Assert.Equal("https://localhost/api/auth/sso/callback", HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["redirect_uri"]);

        _idp.SignInAs("subject-jdoe", "jdoe@contoso.test", "john.doe@contoso.test");
        using var answer = _idp.Authorize(challenge.Headers.Location!);
        var callback = await client.PostAsync("/api/auth/sso/callback", answer, Ct);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/reports/health", callback.Headers.Location!.OriginalString);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.Equal("jdoe@contoso.test", me.GetProperty("userName").GetString());
        Assert.Equal("john.doe@contoso.test", me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("isSingleSignOnAccount").GetBoolean());
        Assert.False(me.GetProperty("isDirectoryAccount").GetBoolean());
        Assert.Empty(me.GetProperty("roles").EnumerateArray());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == "jdoe@contoso.test", Ct);
        Assert.Equal("subject-jdoe", user.SingleSignOnSubject);
        Assert.Null(user.PasswordHash);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "auth.signin" && e.UserId == user.Id && e.Detail == "Single sign-on", Ct));
    }

    [Fact]
    public async Task LaterSignIns_FindTheAccountByItsSubject_AndUpdateTheEmail()
    {
        var first = await SignInAsync(NewClient(_factory), _idp.SignInAs("subject-ann", "ann@contoso.test", "ann@contoso.test"));
        var second = await SignInAsync(NewClient(_factory), _idp.SignInAs("subject-ann", "ann.renamed@contoso.test", "ann.smith@contoso.test"));

        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal("ann@contoso.test", second.GetProperty("userName").GetString());
        Assert.Equal("ann.smith@contoso.test", second.GetProperty("email").GetString());
    }

    [Fact]
    public async Task UpnWithoutEmailClaim_ServesAsTheEmail()
    {
        var me = await SignInAsync(NewClient(_factory), _idp.SignInAs("subject-upn", "upn.only@contoso.test"));

        Assert.Equal("upn.only@contoso.test", me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task ExistingLocalAccount_IsNotTakenOver()
    {
        await _factory.CreateUserAsync("bob@contoso.test", "bob@contoso.test", "Bob-Local-Passw0rd");

        using var client = NewClient(_factory);
        var callback = await CallbackAsync(client, _idp.SignInAs("subject-bob", "bob@contoso.test", "bob@contoso.test"));

        Assert.Equal("/login?sso=refused", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        Assert.Null((await db.Users.SingleAsync(u => u.UserName == "bob@contoso.test", Ct)).SingleSignOnSubject);
    }

    [Fact]
    public async Task UnsafeReturnUrl_GoesToTheWorkspace()
    {
        using var client = NewClient(_factory);
        var callback = await CallbackAsync(client, _idp.SignInAs("subject-eve", "eve@contoso.test"), returnUrl: "//evil.example/");

        Assert.Equal("/", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ProviderError_ReturnsToTheSignInPage()
    {
        using var client = NewClient(_factory);
        var challenge = await client.GetAsync("/api/auth/sso/login", Ct);
        var state = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["state"]!;
        using var answer = new FormUrlEncodedContent(new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = state });

        var callback = await client.PostAsync("/api/auth/sso/callback", answer, Ct);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/login?sso=failed", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ForgedCallback_WithoutTheBrowsersCorrelationCookie_Fails()
    {
        using var victim = NewClient(_factory);
        var challenge = await NewClient(_factory).GetAsync("/api/auth/sso/login", Ct);
        using var answer = _idp.SignInAs("subject-mallory", "mallory@contoso.test").Authorize(challenge.Headers.Location!);

        var callback = await victim.PostAsync("/api/auth/sso/callback", answer, Ct);

        Assert.Equal("/login?sso=failed", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task SingleSignOnAccounts_HaveNoPasswordToSignInWithOrChange()
    {
        using var client = NewClient(_factory);
        await CallbackAsync(client, _idp.SignInAs("subject-pat", "pat@contoso.test"));
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);

        var change = await client.SendJsonAsync(
            HttpMethod.Post, "/api/account/password", new { currentPassword = "Whatever-Passw0rd", newPassword = "Another-Passw0rd!" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var reset = await admin.SendJsonAsync(
            HttpMethod.Post, $"/api/users/{me.GetProperty("id").GetString()}/password", new { password = "Another-Passw0rd!", mustChangePassword = false }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/users", Ct);
        Assert.True(listed.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "pat@contoso.test").GetProperty("isSingleSignOnAccount").GetBoolean());

        // Guessing at a password neither signs in nor counts towards a lockout.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("pat@contoso.test", "Guess-Passw0rd-1", Ct)).StatusCode);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Users.SingleAsync(u => u.UserName == "pat@contoso.test", Ct);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
    }

    [Fact]
    public async Task DisabledAccount_IsRefused()
    {
        var me = await SignInAsync(NewClient(_factory), _idp.SignInAs("subject-dan", "dan@contoso.test"));
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        (await admin.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{me.GetProperty("id").GetString()}", new { email = "dan@contoso.test", role = "User", isDisabled = true }, Ct)).EnsureSuccessStatusCode();

        var callback = await CallbackAsync(NewClient(_factory), _idp.SignInAs("subject-dan", "dan@contoso.test"));

        Assert.Equal("/login?sso=refused", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Groups_DecideWhoMaySignIn_AndWhoIsAnAdministrator()
    {
        await using var factory = await CreateFactoryAsync(
            ("SingleSignOn:RequiredGroups:0", "nkeeper-users"),
            ("SingleSignOn:RequiredGroups:1", "nkeeper-admins"),
            ("SingleSignOn:AdministratorGroups:0", "nkeeper-admins"));

        var outsider = await CallbackAsync(NewClient(factory), _idp.SignInAs("subject-out", "out@contoso.test", null, "sales"));
        Assert.Equal("/login?sso=refused", outsider.Headers.Location!.OriginalString);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.UserName == "out@contoso.test", Ct));
            var refusal = await db.AuditEvents.SingleAsync(e => e.Action == "auth.signin_failed", Ct);
            Assert.Equal("out@contoso.test", refusal.UserName);
            Assert.Contains("required group", refusal.Detail, StringComparison.Ordinal);
        }

        var asAdmin = await SignInAsync(NewClient(factory), _idp.SignInAs("subject-al", "al@contoso.test", null, "NKeeper-Admins"));
        Assert.Equal(["Administrator"], asAdmin.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var asUser = await SignInAsync(NewClient(factory), _idp.SignInAs("subject-al", "al@contoso.test", null, "nkeeper-users"));
        Assert.Empty(asUser.GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task WithoutSingleSignOn_ThereIsNoProviderToStart()
    {
        await using var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        using var client = NewClient(factory);

        var options = await client.GetFromJsonAsync<JsonElement>("/api/auth/options", Ct);
        Assert.Equal(JsonValueKind.Null, options.GetProperty("singleSignOnName").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/sso/login", Ct)).StatusCode);
    }

    private async Task<NKeeperApiFactory> CreateFactoryAsync(params (string Key, string Value)[] settings) =>
        new(await postgres.CreateDatabaseAsync(Ct), settings: FakeIdentityProvider.Settings(settings), configureServices: _idp.Configure);

    /// <summary>
    /// Over https, as browsers require for the provider's SameSite=None correlation cookies; no
    /// automatic redirects, so each hop can be checked.
    /// </summary>
    private static HttpClient NewClient(NKeeperApiFactory factory) =>
        factory.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler());

    /// <summary>The browser's round trip: NKeeper → the provider (who signs in as set up) → the callback.</summary>
    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, FakeIdentityProvider idp, string returnUrl = "/")
    {
        var challenge = await client.GetAsync($"/api/auth/sso/login?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        using var answer = idp.Authorize(challenge.Headers.Location!);
        var callback = await client.PostAsync("/api/auth/sso/callback", answer, Ct);
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        return callback;
    }

    private static async Task<JsonElement> SignInAsync(HttpClient client, FakeIdentityProvider idp)
    {
        using (client)
        {
            var callback = await CallbackAsync(client, idp);
            Assert.Equal("/", callback.Headers.Location!.OriginalString);
            return await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        }
    }
}
