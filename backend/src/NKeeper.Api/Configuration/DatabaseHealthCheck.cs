using Microsoft.Extensions.Diagnostics.HealthChecks;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Configuration;

internal sealed class DatabaseHealthCheck(NKeeperDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to the database.");
}
