using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenRemote.Infrastructure.Identity;
using OpenRemote.Infrastructure.Persistence;

namespace OpenRemote.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenRemoteInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Configure(o => o.ConnectionString = connectionString ?? string.Empty)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection(BootstrapAdminOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "BootstrapAdmin:UserName, BootstrapAdmin:Email and BootstrapAdmin:Password are required when BootstrapAdmin:Enabled is true.")
            .ValidateOnStart();

        // The connection string is validated at startup; tooling such as 'dotnet ef migrations add' runs without one.
        services.AddDbContext<OpenRemoteDbContext>(options => options.UseNpgsql(connectionString));

        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<OpenRemoteDbContext>();

        // Keys live in PostgreSQL so cookies and antiforgery tokens survive container restarts.
        services.AddDataProtection()
            .SetApplicationName("OpenRemote")
            .PersistKeysToDbContext<OpenRemoteDbContext>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DatabaseTicketStore>();
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, ConfigureSessionStore>();
        services.AddScoped<CredentialValidator>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
