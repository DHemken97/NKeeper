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

        builder.HasOne(h => h.Group).WithMany(g => g.Hosts).HasForeignKey(h => h.GroupId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(h => h.Services).WithOne().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(h => h.Tags).WithMany(t => t.Hosts).UsingEntity("HostTags");
        builder.HasMany(h => h.Credentials).WithMany(c => c.Hosts).UsingEntity("HostCredentials");

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

internal sealed class HostGroupConfiguration : IEntityTypeConfiguration<HostGroup>
{
    public void Configure(EntityTypeBuilder<HostGroup> builder)
    {
        builder.ToTable("HostGroups");
        builder.Property(g => g.Name).HasMaxLength(100).IsRequired();
        builder.Property(g => g.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(1000);
        builder.HasIndex(g => g.NormalizedName).IsUnique();
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.Property(t => t.Name).HasMaxLength(50).IsRequired();
        builder.Property(t => t.NormalizedName).HasMaxLength(50).IsRequired();
        builder.HasIndex(t => t.NormalizedName).IsUnique();
    }
}

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("Credentials");
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Username).HasMaxLength(256);
        builder.Property(c => c.Domain).HasMaxLength(256);
        builder.Property(c => c.Notes).HasMaxLength(2000);
        builder.HasIndex(c => c.Name);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
