using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

public sealed class StartupBehaviorTests(PostgresServerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BootstrapAdmin_WhenAccountExists_DoesNotResetPassword()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using (var first = new NKeeperApiFactory(connectionString))
        {
            first.CreateClient(); // starts the host: migrates and creates the administrator
        }

        await using var second = new NKeeperApiFactory(connectionString, adminPassword: "A-Different-Passw0rd");
        var client = second.CreateClient();

        var withNewPassword = await client.LoginAsync(NKeeperApiFactory.AdminUserName, "A-Different-Passw0rd", Ct);
        var withOriginalPassword = await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, withNewPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withOriginalPassword.StatusCode);
    }

    [Fact]
    public async Task Login_ExceedingRateLimit_Returns429WithRetryAfter()
    {
        await using var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), loginPermitLimit: 3);
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            await client.LoginAsync("someone", "wrong-password-123", Ct);
        }

        var limited = await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task ProductionCookieSettings_OverPlainHttp_LoginWorksAndSessionCookieIsSecure()
    {
        // Mirrors Docker on http://localhost: nginx forwards plain HTTP while RequireSecureCookies=true.
        await using var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), requireSecureCookies: true);
        var client = factory.CreateClient(new() { HandleCookies = false });

        var antiforgery = await client.GetAsync("/api/auth/antiforgery", Ct);
        Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
        var token = (await antiforgery.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
        var antiforgeryCookie = antiforgery.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userNameOrEmail = NKeeperApiFactory.AdminUserName, password = NKeeperApiFactory.AdminPassword }),
        };
        request.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);
        request.Headers.Add("Cookie", antiforgeryCookie);
        var login = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(NKeeperApiFactory.SessionCookieName, StringComparison.Ordinal));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessProbe_ReportsHealthyDatabase()
    {
        await using var factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));

        var response = await factory.CreateClient().GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
