using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// A sign-in that passed its password (or single sign-on) and waits for the code emailed to the
/// user. The browser holds a random token for it in a cookie; only the token's hash is stored here.
/// </summary>
public sealed class TwoFactorChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 of the cookie's token.</summary>
    public byte[] TokenHash { get; set; } = [];

    public Guid UserId { get; set; }

    /// <summary>HMAC-SHA-256 of the code, keyed with the token: neither alone reveals it.</summary>
    public byte[] CodeHash { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset LastSentAtUtc { get; set; }

    public int FailedAttempts { get; set; }

    public bool RememberMe { get; set; }

    /// <summary>How the first step went, for the audit trail: "Active Directory", "Single sign-on" or null (password).</summary>
    public string? SignInMethod { get; set; }

    /// <summary>A domain password to keep with the session once signed in (RDP pass-through), encrypted.</summary>
    public byte[]? ProtectedSignInPassword { get; set; }
}

internal sealed class TwoFactorChallengeConfiguration : IEntityTypeConfiguration<TwoFactorChallenge>
{
    public void Configure(EntityTypeBuilder<TwoFactorChallenge> builder)
    {
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.HasIndex(c => c.ExpiresAtUtc);
        builder.Property(c => c.SignInMethod).HasMaxLength(32);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
