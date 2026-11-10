using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Auditing;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Reports;

namespace NKeeper.Api.Controllers;

/// <summary>A report's saved answers, and who saved them last.</summary>
public sealed record ReportAnswersResponse(JsonElement Answers, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy);

public sealed record ReportAnswersRequest
{
    [Required]
    public JsonElement Answers { get; init; }
}

/// <summary>
/// What people typed into a report that asks questions NKeeper can't answer itself (the insurance
/// questionnaire), kept so the next person, or next year's renewal, starts from it. Everyone signed in reads
/// and saves them; saving is audited. The page decides what's in the JSON.
/// </summary>
[ApiController]
[Authorize]
[Route("api/reports/answers/{key:regex(^[[a-z0-9-]]{{1,64}}$)}")]
public sealed class ReportAnswersController(NKeeperDbContext db, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<ReportAnswersResponse> Get(string key, CancellationToken cancellationToken)
    {
        var saved = await db.ReportAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (saved is null)
        {
            return new ReportAnswersResponse(JsonSerializer.SerializeToElement(new { }), null, null);
        }

        var by = saved.UpdatedByUserId is { } userId ? await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync(cancellationToken) : null;
        using var document = JsonDocument.Parse(saved.Json);
        return new ReportAnswersResponse(document.RootElement.Clone(), saved.UpdatedAtUtc, by);
    }

    [HttpPut]
    [Audit("report.answers", AuditTarget.None)]
    public async Task<ActionResult<ReportAnswersResponse>> Save(string key, ReportAnswersRequest request, CancellationToken cancellationToken)
    {
        if (request.Answers.ValueKind != JsonValueKind.Object)
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["answers"] = ["Send the answers as an object."] }));
        }

        var json = request.Answers.GetRawText();
        if (json.Length > ReportAnswers.JsonMaxLength)
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["answers"] = ["The answers are too long to save."] }));
        }

        var saved = await db.ReportAnswers.FirstOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (saved is null)
        {
            saved = new ReportAnswers { Key = key };
            db.ReportAnswers.Add(saved);
        }

        saved.Json = json;
        saved.UpdatedAtUtc = timeProvider.GetUtcNow();
        saved.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);
        return new ReportAnswersResponse(request.Answers, saved.UpdatedAtUtc, User.Identity?.Name);
    }
}
