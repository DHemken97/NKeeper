using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.AuthDrivers;
using NKeeper.Infrastructure.Data;
using NKeeper.Infrastructure.Options;
using NKeeper.Infrastructure.Services;

namespace NKeeper.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNKeeperInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthenticationOptions>().Bind(configuration.GetSection(AuthenticationOptions.SectionName));
        services.AddOptions<ActiveDirectoryOptions>().Bind(configuration.GetSection(ActiveDirectoryOptions.SectionName));
        services.AddOptions<LinuxPamOptions>().Bind(configuration.GetSection(LinuxPamOptions.SectionName));
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => o.SigningKey.Length >= 32,
                $"{JwtOptions.SectionName}:SigningKey must be at least 32 characters. Set it with user-secrets or an environment variable.")
            .Validate(o => o.Audience != o.MfaAudience, "Session and MFA audiences must differ.")
            .ValidateOnStart();

        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<AppDbContext>(db =>
        {
            switch (database.Provider.ToLowerInvariant())
            {
                case "sqlite":
                    db.UseSqlite(connectionString);
                    break;
                case "postgres":
                case "postgresql":
                    db.UseNpgsql(connectionString);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported database provider '{database.Provider}'. Use Sqlite or Postgres.");
            }
        });

        var dataProtection = services.AddDataProtection().SetApplicationName("NKeeper");
        if (!string.IsNullOrWhiteSpace(database.DataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(database.DataProtectionKeysPath));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

        // Authentication drivers. Add new identity sources here; the login endpoint picks by DriverType.
        services.AddScoped<IAuthenticationDriver, LocalDbAuthDriver>();
        services.AddScoped<IAuthenticationDriver, ActiveDirectoryAuthDriver>();
        services.AddScoped<IAuthenticationDriver, LinuxPamAuthDriver>();

        services.AddSingleton<TotpService>();
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<UserProvisioningService>();

        return services;
    }
}
