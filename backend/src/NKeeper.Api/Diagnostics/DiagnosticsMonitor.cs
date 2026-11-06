using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Configuration;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Diagnostics;

/// <summary>
/// Writes a line when NKeeper starts and stops (a start with no stop before it was a crash), how the
/// server is doing every hour, and the day's usage totals.
/// </summary>
internal sealed class DiagnosticsMonitor(
    DiagnosticsLog log, IServiceScopeFactory scopes, IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider, ILogger<DiagnosticsMonitor> logger)
    : BackgroundService
{
    private readonly DateTimeOffset _started = timeProvider.GetUtcNow();
    private DateOnly _day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.Write("start", new
        {
            schema = 1,
            version = AppVersion.Full,
            install = Install(),
            environment = environment.EnvironmentName,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = Environment.Version.ToString(),
            features = new
            {
                activeDirectory = configuration.GetValue<bool>("ActiveDirectory:Enabled"),
                singleSignOn = configuration.GetValue<bool>("SingleSignOn:Enabled"),
                recordTerminalSessions = configuration.GetValue("Auditing:RecordTerminalSessions", true),
                healthChecks = configuration.GetValue("Health:Enabled", true),
                webProxyDomain = !string.IsNullOrWhiteSpace(configuration["WebProxy:BaseDomain"]),
                keysEncrypted = !string.IsNullOrWhiteSpace(configuration["DataProtection:CertificatePath"]),
            },
        });

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't write the hourly diagnostics");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        log.FlushRepeats();
        log.Usage.Flush();
        log.Write("stop", new { uptimeSeconds = Uptime() });
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!log.Enabled)
        {
            return;
        }

        object? database = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            log.SetReserve((await InventoryLimits.ReadAsync(db, cancellationToken)).ZipReserveGigabytes * 1024L * 1024L * 1024L);
            var hosts = await db.Hosts.AsNoTracking().Select(h => h.Name).ToListAsync(cancellationToken);
            var users = await db.Users.AsNoTracking().Select(u => u.UserName).ToListAsync(cancellationToken);
            log.SetKnownNames([.. hosts, .. users.OfType<string>()]);
            var size = await db.Database.SqlQueryRaw<long>("""SELECT pg_database_size(current_database()) AS "Value" """).SingleAsync(cancellationToken);
            var tables = await db.Database.SqlQueryRaw<TableSize>(
                """
                SELECT c.relname AS "Name", pg_total_relation_size(c.oid) AS "Bytes"
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind = 'r' AND n.nspname = current_schema()
                ORDER BY 2 DESC LIMIT 5
                """).ToListAsync(cancellationToken);
            database = new
            {
                megabytes = size / (1024 * 1024),
                largestTables = tables.ToDictionary(t => t.Name, t => t.Bytes / (1024 * 1024)),
                hosts = hosts.Count,
                users = users.Count,
            };
        }
#pragma warning disable CA1031 // A database that's down is just what this line should show.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Couldn't read the database's size for the hourly diagnostics");
        }

        var status = log.Status();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        log.Write("hourly", new
        {
            uptimeSeconds = Uptime(),
            memoryMegabytes = process.WorkingSet64 / (1024 * 1024),
            heapMegabytes = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
            threads = process.Threads.Count,
            fileMegabytes = status.SizeBytes / (1024 * 1024),
            database,
        });
        log.FlushRepeats();

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (today != _day)
        {
            _day = today;
            log.FlushRepeats(forget: true);
            log.Usage.Flush();
        }
    }

    private static string Install() =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "container"
        : Environment.GetEnvironmentVariable("APP_POOL_ID") is not null ? "iis"
        : OperatingSystem.IsLinux() ? "linux"
        : "other";

    private long Uptime() => (long)(timeProvider.GetUtcNow() - _started).TotalSeconds;

    private sealed class TableSize
    {
        public required string Name { get; init; }

        public long Bytes { get; init; }
    }
}
