using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Infrastructure.Identity;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Reports ▸ Insurance questionnaire: the answers people type, saved for the system administrator alone, and audited.</summary>
public sealed class ReportAnswersTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private NKeeperApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Answers_start_empty_and_are_saved_and_audited()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.True(me.GetProperty("isSystemAdministrator").GetBoolean());

        var empty = await admin.GetFromJsonAsync<JsonElement>("/api/reports/answers/insurance", Ct);
        Assert.Empty(empty.GetProperty("answers").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("updatedAtUtc").ValueKind);

        var body = new { answers = new { organisation = "Acme", instances = new Dictionary<string, object> { ["h1:s1"] = new { server = new { managedBy = "DBA team" } } } } };
        (await admin.SendJsonAsync(HttpMethod.Put, "/api/reports/answers/insurance", body, Ct)).EnsureSuccessStatusCode();
        var read = await admin.GetFromJsonAsync<JsonElement>("/api/reports/answers/insurance", Ct);
        Assert.Equal("Acme", read.GetProperty("answers").GetProperty("organisation").GetString());
        Assert.Equal("DBA team", read.GetProperty("answers").GetProperty("instances").GetProperty("h1:s1").GetProperty("server").GetProperty("managedBy").GetString());
        Assert.Equal(NKeeperApiFactory.AdminUserName, read.GetProperty("updatedBy").GetString());

        var audit = await admin.GetStringAsync("/api/audit/events?limit=50", Ct);
        Assert.Contains("report.answers", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_system_administrator_sees_them_not_other_administrators_or_users()
    {
        const string password = "Other-User-Passw0rd";
        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", password);
        await _factory.CreateUserAsync("second", "second@nkeeper.test", password);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByNameAsync("second"))!, SystemRoles.Administrator)).Succeeded);
        }

        foreach (var name in new[] { "plain", "second" })
        {
            using var other = _factory.CreateDefaultClient(new CookieContainerHandler());
            (await other.LoginAsync(name, password, Ct)).EnsureSuccessStatusCode();
            var me = await other.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
            Assert.False(me.GetProperty("isSystemAdministrator").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/reports/answers/insurance", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.SendJsonAsync(HttpMethod.Put, "/api/reports/answers/insurance", new { answers = new { organisation = "x" } }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/mssql/sessions/abc/survey", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Answers_must_be_an_object_under_a_simple_key()
    {
        using var admin = await _factory.CreateSignedInClientAsync(Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendJsonAsync(HttpMethod.Put, "/api/reports/answers/insurance", new { answers = new[] { 1, 2 } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/reports/answers/Not_Allowed", Ct)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_is_needed()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/reports/answers/insurance", Ct)).StatusCode);
    }
}
