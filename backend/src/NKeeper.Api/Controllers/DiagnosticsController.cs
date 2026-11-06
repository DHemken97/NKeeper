using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NKeeper.Api.Auditing;
using NKeeper.Api.Configuration;
using NKeeper.Api.Diagnostics;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Controllers;

/// <param name="Version">The version of NKeeper that's running.</param>
public sealed record DiagnosticsStatusResponse(
    bool Enabled,
    int MaxMegabytes,
    int MinMegabytes,
    int CeilingMegabytes,
    long SizeBytes,
    DateTimeOffset? OldestUtc,
    DateTimeOffset? NewestUtc,
    DateTimeOffset? LastDownloadedUtc,
    DateTimeOffset? DroppedUtc,
    bool PausedForDiskSpace,
    string? WriteError,
    string Directory,
    string Version);

public sealed record DiagnosticsSettingsRequest(
    bool Enabled,
    [Range(DiagnosticsLog.MinMegabytes, DiagnosticsLog.CeilingMegabytes)] int MaxMegabytes);

/// <summary>An error the browser couldn't handle.</summary>
public sealed record BrowserErrorRequest(
    [Required, StringLength(2000)] string Message,
    [StringLength(8000)] string? Stack,
    [StringLength(500)] string? Page);

/// <summary>Settings ▸ Diagnostics: the file of errors and usage totals administrators can download, and the errors browsers report to it.</summary>
[ApiController]
[Authorize]
public sealed partial class DiagnosticsController(DiagnosticsLog log, TimeProvider timeProvider) : ControllerBase
{
    public const string RateLimitPolicy = "browser-errors";

    [HttpGet("api/admin/diagnostics")]
    [Authorize(Roles = SystemRoles.Administrator)]
    public DiagnosticsStatusResponse Get() => ToResponse(log.Status());

    [HttpPut("api/admin/diagnostics")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [Audit("settings.diagnostics", AuditTarget.None)]
    public DiagnosticsStatusResponse Save(DiagnosticsSettingsRequest request)
    {
        log.Update(request.Enabled, request.MaxMegabytes);
        return ToResponse(log.Status());
    }

    [HttpGet("api/admin/diagnostics/download")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [Audit("settings.diagnostics_download", AuditTarget.None)]
    public async Task Download(CancellationToken cancellationToken)
    {
        var stamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        // Closing a zip entry writes its last bytes synchronously, even with the async zip methods.
        HttpContext.Features.GetRequiredFeature<IHttpBodyControlFeature>().AllowSynchronousIO = true;
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"nkeeper-diagnostics-{stamp}.zip\"";
        await log.WriteZipAsync(Response.Body, cancellationToken);
    }

    /// <summary>From every signed-in user's browser, so it's limited in size and rate and everything in it is redacted.</summary>
    [HttpPost("api/diagnostics/browser-errors")]
    [RequestSizeLimit(32_768)]
    [EnableRateLimiting(RateLimitPolicy)]
    public IActionResult ReportBrowserError(BrowserErrorRequest request)
    {
        if (log.Enabled)
        {
            var page = request.Page is null ? null : log.Redactor.Redact(Guid().Replace(request.Page.Split('?', '#')[0], ":id"), keepPaths: true);
            log.RecordError(
                "browser",
                "Error",
                log.Redactor.Redact(request.Message),
                null,
                request.Stack is null ? null : log.Redactor.Redact(request.Stack, keepPaths: true),
                new { page, browser = log.Redactor.Redact(Request.Headers.UserAgent.ToString()) });
        }

        return NoContent();
    }

    private static DiagnosticsStatusResponse ToResponse(DiagnosticsStatus status) => new(
        status.Enabled,
        status.MaxMegabytes,
        DiagnosticsLog.MinMegabytes,
        DiagnosticsLog.CeilingMegabytes,
        status.SizeBytes,
        status.OldestUtc,
        status.NewestUtc,
        status.LastDownloadedUtc,
        status.DroppedUtc,
        status.PausedForDiskSpace,
        status.WriteError,
        status.Directory,
        AppVersion.Display);

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.None, 500)]
    private static partial Regex Guid();
}
