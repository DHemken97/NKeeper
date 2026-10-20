using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Api.Inventory;
using NKeeper.Api.Security;
using NKeeper.Api.Desktop;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;

namespace NKeeper.Api.Configuration;

internal static class ApiServiceCollectionExtensions
{
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";
    public const string RateLimitedCode = "rate_limited";

    public static IServiceCollection AddNKeeperApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthenticationSettings>()
            .Bind(configuration.GetSection(AuthenticationSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .Validate(o => o.IsValid(), "Cors:AllowedOrigins entries must be absolute http(s) origins without a path or trailing slash.")
            .ValidateOnStart();
        services.AddOptions<LoginRateLimitSettings>()
            .Bind(configuration.GetSection(LoginRateLimitSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<GuacdSettings>()
            .Bind(configuration.GetSection(GuacdSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ForwardedHeadersSettings>()
            .Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.IsValid(), "ForwardedHeaders:KnownNetworks entries must be valid CIDR ranges.")
            .ValidateOnStart();

        services.AddControllers(options =>
            {
                options.Filters.Add<ValidateAntiforgeryTokenFilter>();
                options.Filters.Add<AccountStatusFilter>();
                options.Filters.Add<InventoryExceptionFilter>();
                options.Filters.Add<InstanceLockFilter>();
                // Validation error keys use JSON (camelCase) names so clients can map them to fields.
                options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
            })
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        services.AddScoped<HostAccess>();
        services.AddScoped<HostManager>();
        services.AddScoped<NKeeper.Api.Inventory.Import.HostImporter>();
        services.AddSingleton<NKeeper.Api.Inventory.Scanning.ExternalScan>();
        services.AddScoped<NKeeper.Api.Users.UserAdministration>();
        services.AddScoped<SessionTargetResolver>();
        services.AddScoped<NKeeper.Api.Transfer.InstanceExporter>();
        services.AddScoped<NKeeper.Api.Transfer.InstanceImporter>();
        services.AddScoped<NKeeper.Api.Transfer.InstanceLock>();
        services.AddScoped<TrustedKeyStore>();
        services.AddScoped<SshTerminalSession>();
        services.AddScoped<NKeeper.Api.Telnet.TelnetTerminalSession>();
        services.AddScoped<DesktopSession>();
        services.AddScoped<NKeeper.Api.Rpc.RpcSession>();
        services.AddSingleton<SessionLimiter>();
        services.AddHttpContextAccessor();
        services.AddOptions<NKeeper.Api.Auditing.AuditSettings>()
            .Bind(configuration.GetSection(NKeeper.Api.Auditing.AuditSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<NKeeper.Api.Auditing.AuditLog>();
        services.AddHostedService<NKeeper.Api.Auditing.AuditRetention>();
        services.AddOptions<NKeeper.Api.Health.HealthSettings>()
            .Bind(configuration.GetSection(NKeeper.Api.Health.HealthSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<NKeeper.Api.Health.HostHealthChecker>();
        services.AddSingleton<Controllers.SftpDownloadLinks>();
        services.AddHostedService<NKeeper.Api.Health.HealthMonitor>();
        services.AddOptions<WebProxySettings>()
            .Bind(configuration.GetSection(WebProxySettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpForwarder();
        services.AddSingleton<NKeeper.Api.Web.WebSessionStore>();
        services.AddHostedService<NKeeper.Api.Web.WebSessionSweeper>();
        services.AddSingleton<NKeeper.Api.Kvm.ConsoleSessionStore>();
        services.AddHostedService<NKeeper.Api.Kvm.ConsoleSessionSweeper>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier));
        services.AddOpenApi();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = "NKeeper.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Not a credential, so SameAsRequest (Secure whenever served over HTTPS). With "Always",
            // antiforgery throws on plain-HTTP requests such as http://localhost behind nginx.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        // Identity registers the cookie scheme; shape it for a JSON API (no redirects) and server-side sessions.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<AuthenticationSettings>>((options, auth) =>
            {
                var settings = auth.Value;
                options.Cookie.Name = settings.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = SecurePolicy(settings);
                options.ExpireTimeSpan = settings.SessionTimeout;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddNKeeperSingleSignOn(configuration);

        // Secure by default: every endpoint requires an authenticated user unless it opts out.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<CorsSettings>>((options, cors) =>
            {
                if (cors.Value.AllowedOrigins.Length == 0)
                {
                    return;
                }

                options.AddPolicy(CorsSettings.PolicyName, policy => policy
                    .WithOrigins(cors.Value.AllowedOrigins)
                    .WithMethods(HttpMethods.Get, HttpMethods.Post)
                    .WithHeaders("Content-Type", AntiforgeryHeaderName)
                    .AllowCredentials());
            });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitSettings.PolicyName, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<LoginRateLimitSettings>>().Value;
                var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = settings.Window,
                    QueueLimit = 0,
                });
            });
            // Exports and imports are expensive (key derivation) and sensitive: a few per user at a time.
            options.AddPolicy(Controllers.InstanceTransferController.RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many attempts",
                    Detail = context.HttpContext.Request.Path.StartsWithSegments("/api/admin/transfer")
                        ? "Too many export or import attempts. Wait a few minutes and try again."
                        : "Too many sign-in attempts. Wait a moment and try again.",
                };
                problem.Extensions["code"] = RateLimitedCode;
                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext { HttpContext = context.HttpContext, ProblemDetails = problem });
            };
        });

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, forwarded) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = forwarded.Value.ForwardLimit;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var network in forwarded.Value.KnownNetworks)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });

        return services;
    }

    private static CookieSecurePolicy SecurePolicy(AuthenticationSettings settings) =>
        settings.RequireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
}
