using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Hand-written migrations that must keep existing data.</summary>
public sealed class MigrationTests(PostgresServerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GeneralizingTrustedKeys_KeepsTrustedSshHostKeys()
    {
        var options = new DbContextOptionsBuilder<NKeeperDbContext>().UseNpgsql(await postgres.CreateDatabaseAsync(Ct)).Options;
        await using var db = new NKeeperDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AddTrustedSshHostKeys", cancellationToken: Ct);

        var hostId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Hosts" ("Id", "Name", "IpAddress", "CreatedAtUtc", "UpdatedAtUtc") VALUES ({hostId}, 'old', '10.0.0.1', now(), now())""", Ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "HostSshKeys" ("Id", "HostId", "Port", "Algorithm", "Fingerprint", "TrustedAtUtc") VALUES ({Guid.NewGuid()}, {hostId}, 22, 'ssh-ed25519', 'abc', now())""", Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var key = await db.HostTrustedKeys.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ServiceProtocol.Ssh, key.Protocol);
        Assert.Equal(("ssh-ed25519", "abc", 22), (key.Algorithm, key.Fingerprint, key.Port));
    }
}
