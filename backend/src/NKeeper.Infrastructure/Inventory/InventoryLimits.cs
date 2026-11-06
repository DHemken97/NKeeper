using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>
/// How much a host may hold, how scans pre-select what they find, and the disk space folder downloads leave free, set by administrators on the Settings page.
/// A single row (<see cref="SingletonId"/>); until it's first saved, the defaults apply.
/// </summary>
public sealed class InventoryLimits
{
    public const int SingletonId = 1;

    public const int DefaultMaxServicesPerHost = 100;
    public const int DefaultMaxCredentialsPerHost = 100;
    public const int DefaultMaxTagsPerHost = 50;
    public const int DefaultScanUsersPreselectedUpTo = 10;
    public const int DefaultZipReserveGigabytes = 5;

    /// <summary>The most a setting may be raised to: past these, the host editor and its requests get unwieldy.</summary>
    public const int CeilingServices = 1000;
    public const int CeilingCredentials = 1000;
    public const int CeilingTags = 500;
    public const int CeilingScanUsers = 1000;
    public const int CeilingZipReserveGigabytes = 1000;

    public int Id { get; set; } = SingletonId;

    public int MaxServicesPerHost { get; set; } = DefaultMaxServicesPerHost;

    public int MaxCredentialsPerHost { get; set; } = DefaultMaxCredentialsPerHost;

    public int MaxTagsPerHost { get; set; } = DefaultMaxTagsPerHost;

    /// <summary>
    /// A scan that finds this many new users or fewer offers them ticked; more, and they all start unticked
    /// (a server with many accounts rarely needs every one as a credential).
    /// </summary>
    public int ScanUsersPreselectedUpTo { get; set; } = DefaultScanUsersPreselectedUpTo;

    /// <summary>A folder is only zipped for download if the server would still have this much free beyond the folder's size.</summary>
    public int ZipReserveGigabytes { get; set; } = DefaultZipReserveGigabytes;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>The saved limits, or the defaults when none were saved.</summary>
    public static async Task<InventoryLimits> ReadAsync(NKeeperDbContext db, CancellationToken cancellationToken) =>
        await db.InventoryLimits.AsNoTracking().FirstOrDefaultAsync(l => l.Id == SingletonId, cancellationToken) ?? new InventoryLimits();
}

internal sealed class InventoryLimitsConfiguration : IEntityTypeConfiguration<InventoryLimits>
{
    public void Configure(EntityTypeBuilder<InventoryLimits> builder)
    {
        builder.ToTable("InventoryLimits", t => t.HasCheckConstraint("CK_InventoryLimits_Singleton", $"\"Id\" = {InventoryLimits.SingletonId}"));
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
