using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NKeeper.Infrastructure.Auditing;

/// <summary>
/// One thing that happened: a sign-in, a session opened or closed, a host or user changed, data
/// exported. Rows are only ever added (and removed by retention), never changed. The user's and
/// host's names are copied in, so the trail still reads correctly after they're renamed or deleted;
/// there are deliberately no foreign keys.
/// </summary>
public sealed class AuditEvent
{
    public const int DetailMaxLength = 2000;

    public long Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Who did it; null for anonymous events (e.g. a failed sign-in).</summary>
    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>What happened, e.g. "session.start", "host.update"; see <c>AuditActions</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public Guid? HostId { get; set; }

    public string? HostName { get; set; }

    /// <summary>A human-readable summary, e.g. "SSH · root@10.0.0.5:22". Never contains secrets.</summary>
    public string? Detail { get; set; }

    /// <summary>Ties a session's start, end and recording together.</summary>
    public string? SessionId { get; set; }

    /// <summary>The address the request came from.</summary>
    public string? ClientAddress { get; set; }

    /// <summary>For session events: the terminal recording, when there is one.</summary>
    public Guid? RecordingId { get; set; }
}

/// <summary>
/// What an SSH or Telnet session showed, as an asciicast v2 file (gzip-compressed): the terminal's
/// output with timings, for playback. Only output is kept, never keystrokes, so passwords typed at a
/// prompt (which terminals don't echo) aren't in it.
/// </summary>
public sealed class SessionRecording
{
    public Guid Id { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    public Guid? HostId { get; set; }

    public string? HostName { get; set; }

    /// <summary>"SSH" or "Telnet".</summary>
    public string Protocol { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    /// <summary>The gzip-compressed asciicast.</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Size of the uncompressed recording, in bytes.</summary>
    public long Size { get; set; }

    /// <summary>The session went on after the recording reached its size limit.</summary>
    public bool Truncated { get; set; }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.UserName).HasMaxLength(256);
        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.HostName).HasMaxLength(200);
        builder.Property(e => e.Detail).HasMaxLength(AuditEvent.DetailMaxLength);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.Property(e => e.ClientAddress).HasMaxLength(64);
        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasIndex(e => new { e.UserId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.HostId, e.OccurredAtUtc });
        builder.HasIndex(e => e.Action);
    }
}

internal sealed class SessionRecordingConfiguration : IEntityTypeConfiguration<SessionRecording>
{
    public void Configure(EntityTypeBuilder<SessionRecording> builder)
    {
        builder.ToTable("SessionRecordings");
        builder.Property(r => r.SessionId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.UserName).HasMaxLength(256);
        builder.Property(r => r.HostName).HasMaxLength(200);
        builder.Property(r => r.Protocol).HasMaxLength(16).IsRequired();
        builder.HasIndex(r => r.StartedAtUtc);
        builder.HasIndex(r => r.SessionId);
    }
}
