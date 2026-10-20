using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Persistence;

/// <summary>Runs optional startup migrations, secures the stored keys, and the optional bootstrap administrator setup.</summary>
public sealed class DatabaseInitializer(
    NKeeperDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    KeyRingEncryption keyRing,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<BootstrapAdminOptions> bootstrapOptions,
    IHostEnvironment environment,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (databaseOptions.Value.MigrateOnStartup)
        {
            logger.LogInformation("Applying pending database migrations.");
            await db.Database.MigrateAsync(cancellationToken);
        }

        // Before anything loads the keys.
        await keyRing.SecureStoredKeysAsync(cancellationToken);
        await EnsureBootstrapAdministratorAsync(cancellationToken);
    }

    private async Task EnsureBootstrapAdministratorAsync(CancellationToken cancellationToken)
    {
        var options = bootstrapOptions.Value;
        if (!options.Enabled)
        {
            return;
        }

        if (!environment.IsDevelopment() && !options.AllowOutsideDevelopment)
        {
            logger.LogWarning(
                "BootstrapAdmin is enabled but the environment is {Environment}; skipping. " +
                "Set BootstrapAdmin:AllowOutsideDevelopment=true to override deliberately.",
                environment.EnvironmentName);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await roleManager.RoleExistsAsync(SystemRoles.Administrator))
        {
            EnsureSucceeded(
                await roleManager.CreateAsync(new ApplicationRole(SystemRoles.Administrator)),
                "create the Administrator role");
        }

        var existing = await userManager.FindByNameAsync(options.UserName!)
            ?? await userManager.FindByEmailAsync(options.Email!);
        if (existing is not null)
        {
            // Never touch an existing account's password: the bootstrap only creates, it never resets.
            logger.LogInformation("Bootstrap administrator {UserId} already exists; leaving it unchanged.", existing.Id);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = options.UserName,
            Email = options.Email,
            EmailConfirmed = true,
        };

        EnsureSucceeded(await userManager.CreateAsync(admin, options.Password!), "create the bootstrap administrator");
        EnsureSucceeded(await userManager.AddToRoleAsync(admin, SystemRoles.Administrator), "assign the Administrator role");
        logger.LogInformation("Created bootstrap administrator {UserId}.", admin.Id);
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            // Identity error descriptions never include the submitted password.
            var errors = string.Join(" ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to {action}: {errors}");
        }
    }
}
