using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Persistence;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.SingleSignOnSubject).HasMaxLength(256);
        // One account per person at the identity provider.
        builder.HasIndex(u => u.SingleSignOnSubject).IsUnique();
        builder.Ignore(u => u.IsSingleSignOnAccount);
    }
}
