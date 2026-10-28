using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Auditing;
using NKeeper.Sdk.Auditing;
using NKeeper.Api.Kvm;
using NKeeper.Sdk.Consoles;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Security;
using NKeeper.Sdk.Security;
using NKeeper.Api.Transfer;
using NKeeper.Api.Web;
using NKeeper.Sdk.Web;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Controllers;

/// <summary>An import: the exported file, its passphrase, and (to import, not preview) the confirmation.</summary>
public sealed class InstanceImportForm
{
    [Required(ErrorMessage = "Choose the export file.")]
    public IFormFile? File { get; init; }

    [Required(ErrorMessage = "Enter the passphrase.")]
    [StringLength(1024)]
    public string Passphrase { get; init; } = string.Empty;

    /// <summary>Must be true to import (replacing this instance's data); ignored by the preview.</summary>
    public bool Confirm { get; init; }
}

/// <summary>
/// Moving a whole instance to a new server: export everything here into an encrypted file, then
/// import it on a fresh NKeeper. Administrators only. See <c>Transfer/BundleFormat.cs</c> for the file.
/// </summary>
[ApiController]
[Route("api/admin/transfer")]
[Authorize(Roles = SystemRoles.Administrator)]
public sealed class InstanceTransferController(NKeeperDbContext db) : ControllerBase
{
    public const string RateLimitPolicy = "transfer";
    private const long MaxBundleBytes = 256L * 1024 * 1024;

    [HttpGet("status")]
    [ProducesResponseType<TransferStatusResponse>(StatusCodes.Status200OK)]
    public async Task<TransferStatusResponse> Status(CancellationToken cancellationToken)
    {
        var settings = await Lock.GetAsync(cancellationToken);
        var lockedBy = settings?.LockedByUserId is { } id
            ? await db.Users.Where(u => u.Id == id).Select(u => u.UserName).FirstOrDefaultAsync(cancellationToken)
            : null;
        var blocked = await Importer.BlockedReasonAsync(User.GetUserId(), cancellationToken);
        return new TransferStatusResponse(blocked is null, blocked, settings?.IsLocked == true, settings?.LockedAtUtc, lockedBy, InstanceExporter.Version);
    }

    /// <summary>
    /// Downloads everything in this instance as a file encrypted with the passphrase. The caller
    /// confirms with their own password; optionally the instance is locked against changes afterwards.
    /// </summary>
    [Audit("transfer.export", AuditTarget.None)]
    [HttpPost("export")]
    [EnableRateLimiting(RateLimitPolicy)]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(InstanceExportRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken);
        var validator = HttpContext.RequestServices.GetRequiredService<CredentialValidator>();
        if (await validator.ValidateAsync(me.UserName!, request.Password, cancellationToken) is null)
        {
            return Coded(StatusCodes.Status403Forbidden, "password_incorrect", "That isn't your password.");
        }

        var exporter = HttpContext.RequestServices.GetRequiredService<InstanceExporter>();
        var file = await exporter.ExportAsync(me.UserName!, userId, request.Passphrase, cancellationToken);
        if (request.LockAfterExport)
        {
            await Lock.SetAsync(true, userId, cancellationToken);
        }

        Response.Headers.CacheControl = "no-store";
        return File(file, "application/octet-stream", $"nkeeper-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.nkeeper");
    }

    /// <summary>Lets hosts and users be changed again after an export (e.g. the move was called off).</summary>
    [Audit("transfer.unlock", AuditTarget.None)]
    [HttpPost("unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unlock(CancellationToken cancellationToken)
    {
        await Lock.SetAsync(false, User.GetUserId(), cancellationToken);
        return NoContent();
    }

    /// <summary>Decrypts and checks an export file, and says what it holds. Nothing is changed.</summary>
    [HttpPost("import/preview")]
    [EnableRateLimiting(RateLimitPolicy)]
    [RequestSizeLimit(MaxBundleBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
    [ProducesResponseType<ImportSummaryResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ImportSummaryResponse>> Preview([FromForm] InstanceImportForm form, CancellationToken cancellationToken) =>
        RunImportAsync(form, import: false, cancellationToken);

    /// <summary>
    /// Replaces this fresh instance's data with the file's. Every session ends, the caller's
    /// included: they sign in again with their account from the old server.
    /// </summary>
    [Audit("transfer.import", AuditTarget.None)]
    [HttpPost("import")]
    [EnableRateLimiting(RateLimitPolicy)]
    [RequestSizeLimit(MaxBundleBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
    [ProducesResponseType<ImportSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<ImportSummaryResponse>> Import([FromForm] InstanceImportForm form, CancellationToken cancellationToken) =>
        RunImportAsync(form, import: true, cancellationToken);

    private async Task<ActionResult<ImportSummaryResponse>> RunImportAsync(InstanceImportForm form, bool import, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (await Importer.BlockedReasonAsync(userId, cancellationToken) is { } blocked)
        {
            return Coded(StatusCodes.Status409Conflict, "import_not_allowed", blocked);
        }

        if (form.File is not { Length: > 0 and <= MaxBundleBytes } upload)
        {
            return Coded(StatusCodes.Status400BadRequest, "bundle_invalid", "Choose an NKeeper export file of at most 256 MB.");
        }

        if (import && !form.Confirm)
        {
            return Coded(StatusCodes.Status400BadRequest, "confirmation_required", "Confirm that this instance's data will be replaced.");
        }

        var bytes = new byte[upload.Length];
        await using (var stream = upload.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        OpenedBundle bundle;
        try
        {
            bundle = Importer.Open(bytes, form.Passphrase);
        }
        catch (BundleException ex)
        {
            return Coded(StatusCodes.Status400BadRequest, "bundle_invalid", ex.Message);
        }
        finally
        {
            Array.Clear(bytes);
        }

        if (!import)
        {
            return InstanceImporter.Summarize(bundle, imported: false);
        }

        try
        {
            await Importer.ImportAsync(bundle, userId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Coded(StatusCodes.Status409Conflict, "import_not_allowed", ex.Message);
        }

        // The caller's account was replaced: end whatever they still have open here.
        var services = HttpContext.RequestServices;
        services.GetRequiredService<WebSessionStore>().EndAll(userId);
        services.GetRequiredService<ConsoleSessionStore>().EndAll(userId);
        services.GetRequiredService<SessionLimiter>().TerminateAll(userId);
        return InstanceImporter.Summarize(bundle, imported: true);
    }

    private InstanceImporter Importer => HttpContext.RequestServices.GetRequiredService<InstanceImporter>();

    private InstanceLock Lock => HttpContext.RequestServices.GetRequiredService<InstanceLock>();

    private ObjectResult Coded(int status, string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
