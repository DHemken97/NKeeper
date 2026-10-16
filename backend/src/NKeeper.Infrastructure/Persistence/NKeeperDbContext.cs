using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Infrastructure.Persistence;

public sealed class NKeeperDbContext(DbContextOptions<NKeeperDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Host> Hosts => Set<Host>();

    public DbSet<HostService> HostServices => Set<HostService>();

    public DbSet<HostTag> HostTags => Set<HostTag>();

    public DbSet<HostCredential> HostCredentials => Set<HostCredential>();

    public DbSet<HostTrustedKey> HostTrustedKeys => Set<HostTrustedKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(NKeeperDbContext).Assembly);
    }
}
