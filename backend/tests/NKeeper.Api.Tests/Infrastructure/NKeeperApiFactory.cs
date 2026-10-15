using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>Hosts the real API in-memory against a dedicated PostgreSQL database.</summary>
public sealed class NKeeperApiFactory(
    string connectionString,
    int loginPermitLimit = 100,
    string? adminPassword = null,
    bool requireSecureCookies = false)
    : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
    public const string AdminEmail = "admin@nkeeper.test";
    public const string AdminPassword = "Test-Admin-Passw0rd";
    public const string SessionCookieName = "NKeeper.Session";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting applies before Program.cs reads configuration, unlike ConfigureAppConfiguration.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:NKeeper", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Authentication:RequireSecureCookies", requireSecureCookies ? "true" : "false");
        builder.UseSetting("RateLimiting:Login:PermitLimit", loginPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("BootstrapAdmin:Enabled", "true");
        builder.UseSetting("BootstrapAdmin:AllowOutsideDevelopment", "true");
        builder.UseSetting("BootstrapAdmin:UserName", AdminUserName);
        builder.UseSetting("BootstrapAdmin:Email", AdminEmail);
        builder.UseSetting("BootstrapAdmin:Password", adminPassword ?? AdminPassword);
    }

    public async Task CreateUserAsync(string userName, string email, string password)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = userName, Email = email }, password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

public static class AuthClientExtensions
{
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    public static async Task<string> GetAntiforgeryTokenAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/antiforgery", cancellationToken);
        return response!.Token;
    }

    public static async Task<HttpResponseMessage> LoginAsync(
        this HttpClient client, string userNameOrEmail, string password, CancellationToken cancellationToken, bool rememberMe = false)
    {
        var token = await client.GetAntiforgeryTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userNameOrEmail, password, rememberMe }),
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpClient> CreateSignedInClientAsync(this NKeeperApiFactory factory, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();
        (await client.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, cancellationToken)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Sends a JSON request with a fresh antiforgery token, as the frontend does.</summary>
    public static async Task<HttpResponseMessage> SendJsonAsync(
        this HttpClient client, HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var token = await client.GetAntiforgeryTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpResponseMessage> LogoutAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        var token = await client.GetAntiforgeryTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request, cancellationToken);
    }

    private sealed record AntiforgeryTokenDto(string Token, string HeaderName);
}
