using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Infrastructure.Reports;

/// <summary>
/// What people typed into a report that asks questions NKeeper can't answer itself (the insurance questionnaire):
/// one row per report, its answers as the page's JSON, so next year's starts from this year's.
/// </summary>
public sealed class ReportAnswers
{
    public const int KeyMaxLength = 64;

    /// <summary>A JSON document of a few hundred answers is far below this; it stops a runaway page.</summary>
    public const int JsonMaxLength = 2_000_000;

    /// <summary>The report, e.g. "insurance".</summary>
    public string Key { get; set; } = string.Empty;

    public string Json { get; set; } = "{}";

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

internal sealed class ReportAnswersConfiguration : IEntityTypeConfiguration<ReportAnswers>
{
    public void Configure(EntityTypeBuilder<ReportAnswers> builder)
    {
        builder.ToTable("ReportAnswers");
        builder.HasKey(a => a.Key);
        builder.Property(a => a.Key).HasMaxLength(ReportAnswers.KeyMaxLength);
        builder.Property(a => a.Json).IsRequired();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
