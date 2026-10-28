using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Infrastructure.Plugins;

/// <summary>
/// One setting of a plugin: a plain value, or a secret encrypted with
/// <see cref="CredentialProtector"/> (never returned to browsers). Rows of plugins that aren't
/// installed any more are kept, so reinstalling one brings its settings back.
/// </summary>
public sealed class PluginSetting
{
    public const int PluginIdMaxLength = 40;
    public const int NameMaxLength = 100;

    public string PluginId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The value; null for a secret.</summary>
    public string? Value { get; set; }

    /// <summary>The secret, encrypted; null for a plain value.</summary>
    public byte[]? ProtectedValue { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// A value a plugin keeps on a host, such as the host's ID in another system. Deleted with the host.
/// </summary>
public sealed class HostPluginValue
{
    /// <summary>Indexed, so it stays well under PostgreSQL's B-tree entry limit even in 4-byte characters.</summary>
    public const int MaxLength = 512;

    public Guid HostId { get; set; }

    public string PluginId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

internal sealed class PluginSettingConfiguration : IEntityTypeConfiguration<PluginSetting>
{
    public void Configure(EntityTypeBuilder<PluginSetting> builder)
    {
        builder.ToTable("PluginSettings");
        builder.HasKey(s => new { s.PluginId, s.Name });
        builder.Property(s => s.PluginId).HasMaxLength(PluginSetting.PluginIdMaxLength);
        builder.Property(s => s.Name).HasMaxLength(PluginSetting.NameMaxLength);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HostPluginValueConfiguration : IEntityTypeConfiguration<HostPluginValue>
{
    public void Configure(EntityTypeBuilder<HostPluginValue> builder)
    {
        builder.ToTable("HostPluginValues");
        builder.HasKey(v => new { v.HostId, v.PluginId, v.Name });
        builder.Property(v => v.PluginId).HasMaxLength(PluginSetting.PluginIdMaxLength);
        builder.Property(v => v.Name).HasMaxLength(PluginSetting.NameMaxLength);
        builder.Property(v => v.Value).HasMaxLength(HostPluginValue.MaxLength);

        // Finding the host with a value, e.g. the one linked to a Zabbix host.
        builder.HasIndex(v => new { v.PluginId, v.Name, v.Value });
    }
}
