using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace NKeeper.Api.Configuration;

/// <summary>
/// Serves the built web frontend from <c>wwwroot</c> when it is deployed next to the API (e.g.
/// under IIS, without nginx), with the same security and cache headers as the bundled nginx.
/// Container and nginx deployments have no <c>wwwroot</c>, so nothing changes for them.
/// </summary>
public static class SpaHosting
{
    // Keep in sync with frontend/nginx/security-headers.conf.
    private static readonly KeyValuePair<string, string>[] SecurityHeaders =
    [
        new("X-Content-Type-Options", "nosniff"),
        new("X-Frame-Options", "DENY"),
        new("Referrer-Policy", "strict-origin-when-cross-origin"),
        new("Permissions-Policy", "camera=(), microphone=(), geolocation=()"),
        new(
            "Content-Security-Policy",
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-src 'self' http: https:; img-src 'self' data:; " +
            "connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'"),
    ];

    public static bool IsDeployed(IWebHostEnvironment environment) =>
        environment.WebRootPath is { } root && File.Exists(Path.Combine(root, "index.html"));

    /// <summary>Adds NKeeper's security headers to every response (call before the endpoints).</summary>
    public static IApplicationBuilder UseNKeeperSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                foreach (var (name, value) in SecurityHeaders)
                {
                    context.Response.Headers[name] = value;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });

    /// <summary>The frontend's files (call before authentication: they're public).</summary>
    public static IApplicationBuilder UseNKeeperFrontendFiles(this WebApplication app) =>
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(app.Environment.WebRootPath),
            OnPrepareResponse = context =>
            {
                // Hashed build assets never change; everything else must be revalidated.
                context.Context.Response.Headers[HeaderNames.CacheControl] =
                    context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal)
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });

    /// <summary>index.html for client-side routes (never for files, /api or /health).</summary>
    public static void MapNKeeperFrontend(this WebApplication app) =>
        app.MapFallbackToFile("{*path:nonfile:regex(^(?!api/|health/).*$)}", "index.html", new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
        }).AllowAnonymous();
}
