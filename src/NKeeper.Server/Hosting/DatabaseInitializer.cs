using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Data;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Server.Hosting;

public static class DatabaseInitializer
{
    /// <summary>
    /// Creates the schema (when <c>Database:AutoCreate</c> is on) and seeds a local administrator
    /// the first time the app starts with an empty user table.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        if (options.AutoCreate)
        {
            await db.Database.EnsureCreatedAsync();
        }

        if (await db.Users.AnyAsync())
        {
            return;
        }

        var configuredPassword = app.Configuration["Bootstrap:AdminPassword"];
        var password = string.IsNullOrWhiteSpace(configuredPassword) ? GeneratePassword() : configuredPassword;

        var admin = new User
        {
            Username = "admin",
            DisplayName = "Administrator",
            AuthSource = AuthDriverType.LocalDatabase,
            IsAdmin = true
        };
        admin.ExternalId = admin.Id.ToString();
        admin.PasswordHash = services.GetRequiredService<IPasswordHasher<User>>().HashPassword(admin, password);

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            // Shown once, on first start only. Change it after signing in.
            logger.LogWarning(
                "Created initial administrator. Username: admin  Password: {Password}  (shown once; change it after first sign-in)",
                password);
        }
        else
        {
            logger.LogWarning("Created initial administrator 'admin' with the password from Bootstrap:AdminPassword.");
        }
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 20);
    }
}
