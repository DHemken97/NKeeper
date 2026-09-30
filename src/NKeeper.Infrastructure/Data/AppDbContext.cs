using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Core.Entities;

namespace NKeeper.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<MfaCredential> MfaCredentials => Set<MfaCredential>();

    public DbSet<Datacenter> Datacenters => Set<Datacenter>();

    public DbSet<Node> Nodes => Set<Node>();

    public DbSet<ConnectionProfile> ConnectionProfiles => Set<ConnectionProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUser(modelBuilder.Entity<User>());
        ConfigureMfaCredential(modelBuilder.Entity<MfaCredential>());
        ConfigureDatacenter(modelBuilder.Entity<Datacenter>());
        ConfigureNode(modelBuilder.Entity<Node>());
        ConfigureConnectionProfile(modelBuilder.Entity<ConnectionProfile>());
    }

    private static void ConfigureUser(EntityTypeBuilder<User> user)
    {
        user.ToTable("Users");
        user.HasKey(u => u.Id);

        user.Property(u => u.Username).IsRequired().HasMaxLength(256);
        user.Property(u => u.DisplayName).HasMaxLength(256);
        user.Property(u => u.Email).HasMaxLength(320);
        user.Property(u => u.ExternalId).HasMaxLength(256);
        user.Property(u => u.PasswordHash).HasMaxLength(512);
        user.Property(u => u.AuthSource).HasConversion<string>().HasMaxLength(32);

        // The same username may exist in several identity sources (local "admin" vs AD "admin").
        user.HasIndex(u => new { u.AuthSource, u.Username }).IsUnique();
        user.HasIndex(u => new { u.AuthSource, u.ExternalId });

        user.HasMany(u => u.MfaCredentials)
            .WithOne(c => c.User)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMfaCredential(EntityTypeBuilder<MfaCredential> credential)
    {
        credential.ToTable("MfaCredentials");
        credential.HasKey(c => c.Id);

        credential.Property(c => c.Name).IsRequired().HasMaxLength(128);
        credential.Property(c => c.Type).HasConversion<string>().HasMaxLength(16);
        credential.Property(c => c.ProtectedSecret).HasMaxLength(1024);
        credential.Property(c => c.CredentialId).HasMaxLength(1024);

        // Two concurrent verifications of the same code must not both succeed.
        credential.Property(c => c.LastUsedTimeStep).IsConcurrencyToken();

        credential.HasIndex(c => c.UserId);
        credential.HasIndex(c => c.CredentialId);
    }

    private static void ConfigureDatacenter(EntityTypeBuilder<Datacenter> datacenter)
    {
        datacenter.ToTable("Datacenters");
        datacenter.HasKey(d => d.Id);

        datacenter.Property(d => d.Name).IsRequired().HasMaxLength(200);
        datacenter.Property(d => d.Location).HasMaxLength(200);
        datacenter.Property(d => d.Description).HasMaxLength(2000);

        datacenter.HasIndex(d => d.Name).IsUnique();

        datacenter.HasMany(d => d.Nodes)
            .WithOne(n => n.Datacenter)
            .HasForeignKey(n => n.DatacenterId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureNode(EntityTypeBuilder<Node> node)
    {
        node.ToTable("Nodes");
        node.HasKey(n => n.Id);

        node.Property(n => n.Name).IsRequired().HasMaxLength(200);
        node.Property(n => n.Hostname).IsRequired().HasMaxLength(255);
        node.Property(n => n.Description).HasMaxLength(2000);
        node.Property(n => n.OsFamily).HasConversion<string>().HasMaxLength(32);

        node.HasIndex(n => n.Name);
        node.HasIndex(n => n.Hostname);

        node.HasMany(n => n.ConnectionProfiles)
            .WithOne(p => p.Node)
            .HasForeignKey(p => p.NodeId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureConnectionProfile(EntityTypeBuilder<ConnectionProfile> profile)
    {
        profile.ToTable("ConnectionProfiles");
        profile.HasKey(p => p.Id);

        profile.Property(p => p.Name).IsRequired().HasMaxLength(200);
        profile.Property(p => p.Protocol).HasConversion<string>().HasMaxLength(32);
        profile.Property(p => p.Username).HasMaxLength(256);
        profile.Property(p => p.SettingsJson).IsRequired();

        profile.HasIndex(p => p.NodeId);
    }
}
