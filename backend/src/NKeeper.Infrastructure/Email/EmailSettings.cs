using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Email;

/// <summary>How the connection to the SMTP server is protected.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain SMTP upgraded with STARTTLS before signing in (port 587 by default).</summary>
    StartTls,

    /// <summary>TLS from the start, "SMTPS" (port 465 by default).</summary>
    SslOnConnect,

    /// <summary>Unencrypted: only for a relay on the same host or a lab.</summary>
    None,
}

/// <summary>
/// The SMTP server NKeeper sends email through (sign-in codes, test messages), set by administrators
/// on the Settings page. A single row (<see cref="SingletonId"/>); none until first saved.
/// </summary>
public sealed class EmailSettings
{
    public const int SingletonId = 1;
    public const int HostMaxLength = 253;
    public const int AddressMaxLength = 256;

    public int Id { get; set; } = SingletonId;

    /// <summary>Off: nothing is sent, and sign-in codes can't be required.</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Empty when the server takes mail without signing in.</summary>
    public string? UserName { get; set; }

    /// <summary>Encrypted with <see cref="Inventory.CredentialProtector"/>; never returned by the API.</summary>
    public byte[]? ProtectedPassword { get; set; }

    public string FromAddress { get; set; } = string.Empty;

    public string? FromName { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

internal sealed class EmailSettingsConfiguration : IEntityTypeConfiguration<EmailSettings>
{
    public void Configure(EntityTypeBuilder<EmailSettings> builder)
    {
        builder.ToTable("EmailSettings", t => t.HasCheckConstraint("CK_EmailSettings_Singleton", $"\"Id\" = {EmailSettings.SingletonId}"));
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Host).HasMaxLength(EmailSettings.HostMaxLength);
        builder.Property(s => s.Security).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.UserName).HasMaxLength(EmailSettings.AddressMaxLength);
        builder.Property(s => s.FromAddress).HasMaxLength(EmailSettings.AddressMaxLength);
        builder.Property(s => s.FromName).HasMaxLength(128);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
