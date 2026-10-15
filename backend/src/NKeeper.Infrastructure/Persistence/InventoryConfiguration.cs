using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Infrastructure.Persistence;

internal sealed class HostConfiguration : IEntityTypeConfiguration<Host>
{
    public void Configure(EntityTypeBuilder<Host> builder)
    {
        builder.ToTable("Hosts");
        builder.Property(h => h.Name).HasMaxLength(200).IsRequired();
        builder.Property(h => h.Hostname).HasMaxLength(253);
        builder.Property(h => h.IpAddress).HasMaxLength(45);
        builder.Property(h => h.MacAddress).HasMaxLength(17);
        builder.Property(h => h.Description).HasMaxLength(4000);
        builder.ToTable(t => t.HasCheckConstraint("CK_Hosts_Address", "\"Hostname\" IS NOT NULL OR \"IpAddress\" IS NOT NULL"));

        builder.HasIndex(h => h.Name);
        builder.HasIndex(h => h.Hostname);
        builder.HasIndex(h => h.IpAddress);

        builder.HasMany(h => h.Services).WithOne().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(h => h.Tags).WithOne().HasForeignKey(t => t.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(h => h.Credentials).WithOne().HasForeignKey(c => c.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(h => h.SshHostKeys).WithOne().HasForeignKey(k => k.HostId).OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(h => h.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(h => h.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HostServiceConfiguration : IEntityTypeConfiguration<HostService>
{
    public void Configure(EntityTypeBuilder<HostService> builder)
    {
        builder.ToTable("HostServices", t => t.HasCheckConstraint("CK_HostServices_Port", "\"Port\" IS NULL OR (\"Port\" BETWEEN 1 AND 65535)"));
        builder.Property(s => s.Protocol).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.Label).HasMaxLength(100);
        builder.HasIndex(s => new { s.HostId, s.Position });
        builder.HasIndex(s => s.Protocol);
    }
}

internal sealed class HostTagConfiguration : IEntityTypeConfiguration<HostTag>
{
    public void Configure(EntityTypeBuilder<HostTag> builder)
    {
        builder.ToTable("HostTags");
        builder.Property(t => t.Name).HasMaxLength(HostTag.NameMaxLength).IsRequired();
        builder.Property(t => t.Value).HasMaxLength(HostTag.ValueMaxLength).IsRequired();
        builder.HasIndex(t => new { t.HostId, t.Name, t.Value }).IsUnique();
        // Supports "every host with tag env" / "env: production" lookups across hosts.
        builder.HasIndex(t => new { t.Name, t.Value });
    }
}

internal sealed class HostSshKeyConfiguration : IEntityTypeConfiguration<HostSshKey>
{
    public void Configure(EntityTypeBuilder<HostSshKey> builder)
    {
        builder.ToTable("HostSshKeys");
        builder.Property(k => k.Algorithm).HasMaxLength(64).IsRequired();
        builder.Property(k => k.Fingerprint).HasMaxLength(64).IsRequired();
        builder.HasIndex(k => new { k.HostId, k.Port, k.Algorithm }).IsUnique();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(k => k.TrustedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HostCredentialConfiguration : IEntityTypeConfiguration<HostCredential>
{
    public void Configure(EntityTypeBuilder<HostCredential> builder)
    {
        builder.ToTable("HostCredentials");
        builder.Property(c => c.Label).HasMaxLength(100);
        builder.Property(c => c.Username).HasMaxLength(256);
        builder.Property(c => c.Domain).HasMaxLength(256);
        builder.Property(c => c.Notes).HasMaxLength(2000);
        builder.HasIndex(c => new { c.HostId, c.Position });
    }
}
