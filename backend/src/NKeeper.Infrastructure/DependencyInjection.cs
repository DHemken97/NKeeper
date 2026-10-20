using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNKeeperInfrastructure(this IServiceCollection services, IConfiguration configuration)
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

        services.AddOptions<ActiveDirectoryOptions>()
            .Bind(configuration.GetSection(ActiveDirectoryOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "ActiveDirectory:Domain and ActiveDirectory:DnsDomain are required when ActiveDirectory:Enabled is true, and ActiveDirectory:Transport None needs ActiveDirectory:AllowInsecureTransport.")
            .ValidateOnStart();

        services.AddOptions<SingleSignOnOptions>()
            .Bind(configuration.GetSection(SingleSignOnOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "SingleSignOn:Authority (an https:// URL) and SingleSignOn:ClientId are required when SingleSignOn:Enabled is true, and its display name and claim names can't be empty.")
            .ValidateOnStart();

        // The connection string is validated at startup; tooling such as 'dotnet ef migrations add' runs without one.
        services.AddDbContext<NKeeperDbContext>(options => options.UseNpgsql(connectionString));

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
            .AddEntityFrameworkStores<NKeeperDbContext>();

        // Keys live in PostgreSQL so cookies and antiforgery tokens survive container restarts. With
        // DataProtection:CertificatePath set, they're encrypted with a certificate kept outside it.
        var keyRing = KeyRingCertificate.Load(configuration);
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("NKeeper")
            .PersistKeysToDbContext<NKeeperDbContext>();
        if (keyRing.Certificate is { } keyCertificate)
        {
            dataProtection.ProtectKeysWithCertificate(keyCertificate);
        }

        services.AddSingleton(keyRing);
        services.AddScoped<KeyRingEncryption>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DatabaseTicketStore>();
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, ConfigureSessionStore>();
        services.AddScoped<CredentialValidator>();
        services.AddSingleton<IDirectoryAuthenticator, LdapDirectoryAuthenticator>();
        services.AddScoped<SingleSignOnAccounts>();
        services.AddScoped<SignInCredentialStore>();
        services.AddScoped<SessionRevoker>();
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<CredentialProtector>();

        return services;
    }
}
