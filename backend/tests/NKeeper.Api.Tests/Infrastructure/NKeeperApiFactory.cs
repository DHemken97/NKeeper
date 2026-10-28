using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>Hosts the real API in-memory against a dedicated PostgreSQL database.</summary>
public sealed class NKeeperApiFactory(
    string connectionString,
    int loginPermitLimit = 100,
    string? adminPassword = null,
    bool requireSecureCookies = false,
    IReadOnlyDictionary<string, string>? settings = null,
    IDirectoryAuthenticator? directory = null,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
    public const string AdminEmail = "admin@nkeeper.test";
    public const string AdminPassword = "Test-Admin-Passw0rd";
    public const string SessionCookieName = "NKeeper.Session";

    /// <summary>NKeeper's own plugins, referenced by the test project and loaded from its own files, unsigned.</summary>
    private static readonly string[] BuiltInPlugins = ["NKeeper.Plugins.Idrac", "NKeeper.Plugins.Zabbix"];

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
        for (var i = 0; i < BuiltInPlugins.Length; i++)
        {
            builder.UseSetting($"Plugins:BuiltIn:{i}", BuiltInPlugins[i]);
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        if (directory is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(directory));
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
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
