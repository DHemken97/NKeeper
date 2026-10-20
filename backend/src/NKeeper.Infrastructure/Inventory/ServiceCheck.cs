using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>
/// The latest health check of one of a host's services: whether it answered, how fast, and (for
/// TLS services) when its certificate expires. Keyed by protocol, port and address rather than by
/// service row, because editing a host replaces its service rows.
/// </summary>
public sealed class ServiceCheck
{
    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    public ServiceProtocol Protocol { get; set; }

    public int Port { get; set; }

    /// <summary>The address checked when it isn't the host's (an iDRAC's); empty otherwise.</summary>
    public string Address { get; set; } = string.Empty;

    public bool IsUp { get; set; }

    public int? LatencyMs { get; set; }

    /// <summary>Why it's down, e.g. "connection refused".</summary>
    public string? Error { get; set; }

    public DateTimeOffset CheckedAtUtc { get; set; }

    /// <summary>When it was first found down, in the current run of failed checks; null while up.</summary>
    public DateTimeOffset? DownSinceUtc { get; set; }

    public DateTimeOffset? CertificateExpiresUtc { get; set; }

    public string? CertificateSubject { get; set; }
}

internal sealed class ServiceCheckConfiguration : IEntityTypeConfiguration<ServiceCheck>
{
    public void Configure(EntityTypeBuilder<ServiceCheck> builder)
    {
        builder.ToTable("ServiceChecks");
        builder.Property(c => c.Protocol).HasConversion(Persistence.ProtocolConversion.Converter).HasMaxLength(32);
        builder.Property(c => c.Address).HasMaxLength(253);
        builder.Property(c => c.Error).HasMaxLength(500);
        builder.Property(c => c.CertificateSubject).HasMaxLength(500);
        builder.HasIndex(c => new { c.HostId, c.Protocol, c.Port, c.Address }).IsUnique();
        builder.HasOne<Host>().WithMany().HasForeignKey(c => c.HostId).OnDelete(DeleteBehavior.Cascade);
    }
}
