using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Infrastructure.Persistence;

using NKeeper.Sdk.Auditing;

namespace NKeeper.Api.Auditing;

/// <summary>Deletes audit events and recordings past their retention, once at start-up and then daily.</summary>
internal sealed class AuditRetention(IServiceScopeFactory scopes, IOptions<AuditSettings> settings, TimeProvider timeProvider, ILogger<AuditRetention> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't apply the audit retention");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        var now = timeProvider.GetUtcNow();
        var events = await db.AuditEvents.Where(e => e.OccurredAtUtc < now.AddDays(-settings.Value.RetentionDays)).ExecuteDeleteAsync(cancellationToken);
        var recordings = await db.SessionRecordings.Where(r => r.StartedAtUtc < now.AddDays(-settings.Value.RecordingRetentionDays)).ExecuteDeleteAsync(cancellationToken);
        if (events + recordings > 0)
        {
            logger.LogInformation("Audit retention removed {Events} events and {Recordings} recordings", events, recordings);
        }
    }
}
