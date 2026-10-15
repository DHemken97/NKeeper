using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Infrastructure;
using NKeeper.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNKeeperInfrastructure(builder.Configuration);
builder.Services.AddNKeeperApi(builder.Configuration);

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value.Enabled)
{
    app.UseForwardedHeaders();
}

// Unhandled exceptions and empty error responses become RFC 7807 problem details.
// Stack traces are only shown by the developer exception page in Development.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseCors(CorsSettings.PolicyName);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Terminal sessions (/api/ssh/session). Pings keep idle sessions alive through proxies.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
}

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
