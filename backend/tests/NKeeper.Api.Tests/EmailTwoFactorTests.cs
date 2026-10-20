using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Email settings, and sign-in codes sent by email ("Email 2FA challenge code").</summary>
public sealed class EmailTwoFactorTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private const string Password = "Two-Factor-Passw0rd";
    private const string Email = "tfa@nkeeper.test";

    private readonly FakeEmailTransport _mail = new();
    private readonly FakeIdentityProvider _idp = new();
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            settings: FakeIdentityProvider.Settings(),
            configureServices: services =>
            {
                services.AddSingleton<IEmailTransport>(_mail);
                _idp.Configure(services);
            });

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _idp.Dispose();
    }

    [Fact]
    public async Task EmailSettings_AreForAdministrators_AndNeverReturnThePassword()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        await SaveEmailSettingsAsync(admin);

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/admin/email", Ct);
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("smtp.nkeeper.test", settings.GetProperty("host").GetString());
        Assert.Equal("SslOnConnect", settings.GetProperty("security").GetString());
        Assert.True(settings.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("smtp-secret", settings.GetRawText(), StringComparison.Ordinal);

        // Saving or testing without a password keeps (uses) the saved one.
        (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/email", EmailSettingsBody(password: null), Ct)).EnsureSuccessStatusCode();
        (await admin.SendJsonAsync(HttpMethod.Post, "/api/admin/email/test", EmailSettingsBody(password: null), Ct)).EnsureSuccessStatusCode();
        Assert.Equal("smtp-secret", _mail.Sent.Last().Server.Password);

        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", Password);
        using var plain = NewClient();
        (await plain.LoginAsync("plain", Password, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/email", Ct)).StatusCode);
    }

    [Fact]
    public async Task TestEmail_GoesToTheAdministrator_WithTheSettingsInTheForm()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);

        var response = await admin.SendJsonAsync(HttpMethod.Post, "/api/admin/email/test", EmailSettingsBody(host: "unsaved.nkeeper.test"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (server, message) = Assert.Single(_mail.Sent);
        Assert.Equal("unsaved.nkeeper.test", server.Host);
        Assert.Equal(NKeeperApiFactory.AdminEmail, message.To);

        _mail.Fail = true;
        var failed = await admin.SendJsonAsync(HttpMethod.Post, "/api/admin/email/test", EmailSettingsBody(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Contains("Connection refused", await failed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInCodes_NeedEmailSetUp_AndEmailCantBeTurnedOffUnderThem()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var create = await admin.SendJsonAsync(HttpMethod.Post, "/api/users", NewUserBody(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Contains("emailTwoFactor", await create.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await SaveEmailSettingsAsync(admin);
        (await admin.SendJsonAsync(HttpMethod.Post, "/api/users", NewUserBody(), Ct)).EnsureSuccessStatusCode();

        var off = await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/email", EmailSettingsBody(enabled: false), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
    }

    [Fact]
    public async Task SignIn_WaitsForTheEmailedCode()
    {
        await CreateTwoFactorUserAsync();
        using var client = NewClient();

        var login = await client.LoginAsync("tfa", Password, Ct);

        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        var challenge = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(challenge.GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal("t•••@nkeeper.test", challenge.GetProperty("codeSentTo").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/two-factor", Ct)).StatusCode);

        var code = _mail.LastCode(Email);
        Assert.Matches("^[0-9]{9}$", code);
        var wrong = await client.SendJsonAsync(HttpMethod.Post, "/api/auth/two-factor", new { code = WrongCode(code) }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_code", (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());

        // Spaces and dashes, as the code is shown in the email, are fine.
        var right = await client.SendJsonAsync(HttpMethod.Post, "/api/auth/two-factor", new { code = $"{code[..3]} {code[3..6]}-{code[6..]}" }, Ct);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal("tfa", (await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("userName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/two-factor", Ct)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "auth.signin" && e.UserName == "tfa" && e.Detail == "emailed code", Ct));
        Assert.False(await db.TwoFactorChallenges.AnyAsync(Ct));
    }

    [Fact]
    public async Task FiveWrongCodes_CancelTheSignIn()
    {
        await CreateTwoFactorUserAsync();
        using var client = NewClient();
        await client.LoginAsync("tfa", Password, Ct);
        var code = _mail.LastCode(Email);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("invalid_code", await VerifyCodeAsync(client, WrongCode(code)));
        }

        Assert.Equal("challenge_expired", await VerifyCodeAsync(client, WrongCode(code)));
        Assert.Equal("challenge_expired", await VerifyCodeAsync(client, code));
    }

    [Fact]
    public async Task Resend_ReplacesTheCode_ButNotStraightAway()
    {
        await CreateTwoFactorUserAsync();
        using var client = NewClient();
        await client.LoginAsync("tfa", Password, Ct);

        var resend = await client.SendJsonAsync(HttpMethod.Post, "/api/auth/two-factor/resend", null, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, resend.StatusCode);
        Assert.NotNull(resend.Headers.RetryAfter);
    }

    [Fact]
    public async Task EmailDown_FailsTheSignIn_WithoutStartingASession()
    {
        await CreateTwoFactorUserAsync();
        _mail.Fail = true;
        using var client = NewClient();

        var login = await client.LoginAsync("tfa", Password, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal("email_unavailable", (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task SingleSignOn_AlsoWaitsForTheCode()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        await SaveEmailSettingsAsync(admin);
        using var first = NewClient(https: true);
        await SingleSignOnAsync(first);
        await EnableTwoFactorAsync(admin, "sso@contoso.test");

        using var client = NewClient(https: true);
        var callback = await SingleSignOnAsync(client);

        Assert.Equal("/login?twoFactor=1", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
        (await client.SendJsonAsync(HttpMethod.Post, "/api/auth/two-factor", new { code = _mail.LastCode("sso@contoso.test") }, Ct)).EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("isSingleSignOnAccount").GetBoolean());
    }

    private HttpClient NewClient(bool https = false) =>
        https ? _factory.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler()) : _factory.CreateDefaultClient(new CookieContainerHandler());

    private async Task<HttpResponseMessage> SingleSignOnAsync(HttpClient client)
    {
        var challenge = await client.GetAsync("/api/auth/sso/login", Ct);
        using var answer = _idp.SignInAs("subject-sso", "sso@contoso.test").Authorize(challenge.Headers.Location!);
        return await client.PostAsync("/api/auth/sso/callback", answer, Ct);
    }

    private async Task CreateTwoFactorUserAsync()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        await SaveEmailSettingsAsync(admin);
        (await admin.SendJsonAsync(HttpMethod.Post, "/api/users", NewUserBody(), Ct)).EnsureSuccessStatusCode();
    }

    private static async Task SaveEmailSettingsAsync(HttpClient admin) =>
        (await admin.SendJsonAsync(HttpMethod.Put, "/api/admin/email", EmailSettingsBody(), Ct)).EnsureSuccessStatusCode();

    private static async Task EnableTwoFactorAsync(HttpClient admin, string userName)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users", Ct);
        var user = users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == userName);
        (await admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{user.GetProperty("id").GetString()}", new
        {
            email = user.GetProperty("email").GetString(),
            role = "User",
            isDisabled = false,
            emailTwoFactor = true,
        }, Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<string?> VerifyCodeAsync(HttpClient client, string code)
    {
        var response = await client.SendJsonAsync(HttpMethod.Post, "/api/auth/two-factor", new { code }, Ct);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();
    }

    private static string WrongCode(string code) => code == "000000000" ? "000000001" : "000000000";

    private static object NewUserBody() =>
        new { userName = "tfa", email = Email, role = "User", password = Password, mustChangePassword = false, emailTwoFactor = true };

    private static object EmailSettingsBody(bool enabled = true, string host = "smtp.nkeeper.test", string? password = "smtp-secret") => new
    {
        enabled,
        host,
        port = 465,
        security = "SslOnConnect",
        userName = "nkeeper",
        password,
        fromAddress = "nkeeper@nkeeper.test",
        fromName = "NKeeper",
    };
}
