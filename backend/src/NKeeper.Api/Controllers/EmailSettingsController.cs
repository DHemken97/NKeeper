using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Settings;
using NKeeper.Api.Inventory;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Controllers;

/// <summary>The SMTP server NKeeper sends email through (sign-in codes). Administrators only.</summary>
[ApiController]
[BlockedWhileInstanceLocked]
[Route("api/admin/email")]
[Authorize(Roles = SystemRoles.Administrator)]
public sealed class EmailSettingsController(
    NKeeperDbContext db,
    EmailSender email,
    CredentialProtector protector,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<EmailSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<EmailSettingsResponse> Get(CancellationToken cancellationToken) =>
        ToResponse(await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == EmailSettings.SingletonId, cancellationToken));

    [Audit("settings.email", AuditTarget.None)]
    [HttpPut]
    [ProducesResponseType<EmailSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<EmailSettingsResponse> Update(EmailSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!request.Enabled && await db.Users.AnyAsync(u => u.TwoFactorEnabled, cancellationToken))
        {
            // They couldn't sign in anymore.
            throw new InventoryValidationException("enabled", "Some users need a sign-in code sent by email. Turn \"Email 2FA challenge code\" off for them first.");
        }

        var settings = await db.EmailSettings.FirstOrDefaultAsync(s => s.Id == EmailSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = new EmailSettings();
            db.EmailSettings.Add(settings);
        }

        Apply(settings, request);
        settings.UpdatedAtUtc = timeProvider.GetUtcNow();
        settings.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(settings);
    }

    /// <summary>
    /// Sends a test message to the signed-in administrator with the settings in the form (not
    /// necessarily saved; an empty password means the saved one).
    /// </summary>
    [Audit("settings.email_test", AuditTarget.None)]
    [HttpPost("test")]
    [ProducesResponseType<EmailTestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EmailTestResponse>> Test(EmailSettingsRequest request, CancellationToken cancellationToken)
    {
        var to = (await userManager.GetUserAsync(User))?.Email;
        if (string.IsNullOrEmpty(to))
        {
            throw new InventoryValidationException("host", "Your account has no email address to send the test to.");
        }

        if (string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.FromAddress))
        {
            throw new InventoryValidationException(string.IsNullOrWhiteSpace(request.Host) ? "host" : "fromAddress", "Fill in the SMTP server and the sender address first.");
        }

        var saved = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == EmailSettings.SingletonId, cancellationToken);
        var draft = new EmailSettings { ProtectedPassword = saved?.ProtectedPassword };
        Apply(draft, request);
        var server = EmailSender.ToServer(draft, draft.ProtectedPassword is null ? null : protector.Unprotect(draft.ProtectedPassword));
        try
        {
            await email.SendAsync(server, new EmailMessage(
                to, "NKeeper test email", "This is a test email from NKeeper. Email is working: sign-in codes can be sent with these settings."), cancellationToken);
        }
        catch (EmailException ex)
        {
            throw new InventoryValidationException("host", ex.Message);
        }

        return new EmailTestResponse(to);
    }

    private void Apply(EmailSettings settings, EmailSettingsRequest request)
    {
        settings.Enabled = request.Enabled;
        settings.Host = request.Host.Trim();
        settings.Port = request.Port;
        settings.Security = request.Security;
        settings.UserName = string.IsNullOrWhiteSpace(request.UserName) ? null : request.UserName.Trim();
        settings.FromAddress = request.FromAddress.Trim();
        settings.FromName = string.IsNullOrWhiteSpace(request.FromName) ? null : request.FromName.Trim();
        if (!string.IsNullOrEmpty(request.Password))
        {
            settings.ProtectedPassword = protector.Protect(request.Password);
        }
        else if (request.ClearPassword)
        {
            settings.ProtectedPassword = null;
        }
    }

    private static EmailSettingsResponse ToResponse(EmailSettings? settings) =>
        settings is null
            ? new EmailSettingsResponse(false, string.Empty, 587, SmtpSecurity.StartTls, null, false, string.Empty, null, null)
            : new EmailSettingsResponse(
                settings.Enabled, settings.Host, settings.Port, settings.Security, settings.UserName, settings.ProtectedPassword is not null,
                settings.FromAddress, settings.FromName, settings.UpdatedAtUtc);
}
