using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>
/// Settings for the whole instance, in a single row (<see cref="SingletonId"/>). Today: whether the
/// instance is locked because its data was exported to move it to a new server, so that changes made
/// here afterwards aren't lost.
/// </summary>
public sealed class InstanceSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Hosts and users can't be changed while locked; sessions still work.</summary>
    public bool IsLocked { get; set; }

    public DateTimeOffset? LockedAtUtc { get; set; }

    public Guid? LockedByUserId { get; set; }
}

internal sealed class InstanceSettingsConfiguration : IEntityTypeConfiguration<InstanceSettings>
{
    public void Configure(EntityTypeBuilder<InstanceSettings> builder)
    {
        builder.ToTable("InstanceSettings", t => t.HasCheckConstraint("CK_InstanceSettings_Singleton", $"\"Id\" = {InstanceSettings.SingletonId}"));
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.LockedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
