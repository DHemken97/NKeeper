using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// End-to-end tests of the auth endpoints against real PostgreSQL. Every test gets a fresh,
/// fully migrated database, so lockout and rate-limit state never leaks between tests.
/// </summary>
public sealed class AuthEndpointTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Login_WithValidUserName_ReturnsProfileAndSetsSecureSessionCookie()
    {
        var client = _factory.CreateClient();

        var response = await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(Ct);
        Assert.Equal(NKeeperApiFactory.AdminUserName, user!.UserName);
        Assert.Equal(NKeeperApiFactory.AdminEmail, user.Email);
        Assert.Contains("Administrator", user.Roles);

        var cookie = GetSessionSetCookie(response);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // session cookie without Remember Me
    }

    [Fact]
    public async Task Login_WithEmailAndRememberMe_IssuesPersistentCookie()
    {
        var client = _factory.CreateClient();

        var response = await client.LoginAsync(NKeeperApiFactory.AdminEmail, NKeeperApiFactory.AdminPassword, Ct, rememberMe: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("expires=", GetSessionSetCookie(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithWrongPasswordOrUnknownUser_ReturnsIdenticalGenericError()
    {
        var client = _factory.CreateClient();

        var wrongPassword = await client.LoginAsync(NKeeperApiFactory.AdminUserName, "not-the-password-123", Ct);
        var unknownUser = await client.LoginAsync("nobody@nkeeper.test", "not-the-password-123", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        var first = await ReadProblemAsync(wrongPassword);
        var second = await ReadProblemAsync(unknownUser);
        Assert.Equal("invalid_credentials", first.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("detail").GetString(), second.GetProperty("detail").GetString());
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie") && GetSetCookies(wrongPassword).Any(IsSessionCookie));
    }

    [Fact]
    public async Task Login_WithMissingFields_ReturnsValidationProblem()
    {
        var client = _factory.CreateClient();

        var response = await client.LoginAsync("", "", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("userNameOrEmail", out _));
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { userNameOrEmail = NKeeperApiFactory.AdminUserName, password = NKeeperApiFactory.AdminPassword },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf_token_invalid", (await ReadProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_AfterRepeatedFailures_LocksAccount()
    {
        await _factory.CreateUserAsync("operator", "operator@nkeeper.test", "Operator-Passw0rd");
        var client = _factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.LoginAsync("operator", "wrong-password-123", Ct);
        }

        var response = await client.LoginAsync("operator", "Operator-Passw0rd", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutSession_ReturnsUnauthorizedProblem()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await ReadProblemAsync(response)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Me_WithSession_ReturnsOnlySafeProfileFields()
    {
        var client = _factory.CreateClient();
        await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct);

        var response = await client.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["email", "id", "isDirectoryAccount", "mustChangePassword", "roles", "signInAccount", "userName"], properties);
        Assert.True(Guid.TryParse(json.RootElement.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task Logout_InvalidatesServerSession_EvenIfOldCookieIsReplayed()
    {
        var client = _factory.CreateClient();
        var login = await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct);
        var sessionCookie = GetSessionSetCookie(login).Split(';')[0];

        var logout = await client.LogoutAsync(Ct);
        var meAfterLogout = await client.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterLogout.StatusCode);

        // A copy of the cookie captured before logout must no longer authenticate.
        var replay = _factory.CreateClient(new() { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", sessionCookie);
        var replayed = await replay.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutSession_IsIdempotent()
    {
        var client = _factory.CreateClient();

        var response = await client.LogoutAsync(Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static IEnumerable<string> GetSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static bool IsSessionCookie(string setCookie) =>
        setCookie.StartsWith(NKeeperApiFactory.SessionCookieName + "=", StringComparison.Ordinal);

    private static string GetSessionSetCookie(HttpResponseMessage response) =>
        GetSetCookies(response).Single(IsSessionCookie);

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private sealed record UserDto(Guid Id, string UserName, string? Email, string[] Roles);
}
