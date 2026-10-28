using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Zabbix;

/// <summary>
/// The Zabbix server NKeeper reads host information from, through its JSON-RPC API with an API
/// token, set by administrators on the Settings page. A single row (<see cref="SingletonId"/>);
/// none until first saved.
/// </summary>
public sealed class ZabbixSettings
{
    public const int SingletonId = 1;
    public const int UrlMaxLength = 2048;

    public int Id { get; set; } = SingletonId;

    /// <summary>Off: Zabbix information isn't shown, and hosts keep their Zabbix host IDs.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Zabbix frontend's address, e.g. <c>https://zabbix.example.com/zabbix</c>; the API is <c>api_jsonrpc.php</c> under it.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Encrypted with <see cref="Inventory.CredentialProtector"/>; never returned by the API.</summary>
    public byte[]? ProtectedApiToken { get; set; }

    /// <summary>
    /// SHA-256 of the HTTPS certificate an administrator trusted (colon-separated upper-case hex),
    /// when it isn't publicly valid. A different certificate is refused.
    /// </summary>
    public string? TrustedCertificateFingerprint { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

internal sealed class ZabbixSettingsConfiguration : IEntityTypeConfiguration<ZabbixSettings>
{
    public void Configure(EntityTypeBuilder<ZabbixSettings> builder)
    {
        builder.ToTable("ZabbixSettings", t => t.HasCheckConstraint("CK_ZabbixSettings_Singleton", $"\"Id\" = {ZabbixSettings.SingletonId}"));
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Url).HasMaxLength(ZabbixSettings.UrlMaxLength);
        builder.Property(s => s.TrustedCertificateFingerprint).HasMaxLength(128);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
