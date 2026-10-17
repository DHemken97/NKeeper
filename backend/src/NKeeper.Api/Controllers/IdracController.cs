using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Api.Configuration;
using NKeeper.Api.Idrac;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

/// <summary>An iDRAC request: which host/service/credential, and optionally a certificate to trust.</summary>
public record IdracRequest : SessionStartMessage
{
    /// <summary>The iDRAC's certificate fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

public sealed record IdracPowerRequest : IdracRequest
{
    /// <summary>A Redfish reset type the server accepts, e.g. "On", "GracefulShutdown", "ForceRestart".</summary>
    [Required(ErrorMessage = "Choose a power action.")]
    [StringLength(32)]
    public string ResetType { get; init; } = string.Empty;
}

/// <summary>Either the server's overview, or the iDRAC's certificate needs to be trusted first.</summary>
public sealed record IdracOverviewResponse(string Status, IdracOverview? Overview, string? Target, WebCertificateResponse? Certificate);

/// <summary>
/// Dell iDRACs, at their own management address: the server's status and power over Redfish, and
/// its HTML5 virtual console in a web session (NKeeper relays it, so the browser never contacts the
/// iDRAC). RACADM, the iDRAC's SSH command line, is an SSH session on the same service.
/// </summary>
[ApiController]
[Authorize]
public sealed class IdracController(WebSessionStore sessions, IOptions<WebProxySettings> settings) : ControllerBase
{
    private const int DefaultHttpsPort = 443;

    [HttpPost("api/idrac/overview")]
    [ProducesResponseType<IdracOverviewResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<IdracOverviewResponse>> Overview(IdracRequest request, CancellationToken cancellationToken) =>
        WithRedfishAsync(request, async (client, target) =>
            new IdracOverviewResponse("ready", await IdracRedfish.GetOverviewAsync(client, cancellationToken), Display(target), null),
            cancellationToken);

    /// <summary>Presses one of the server's power buttons (Redfish ComputerSystem.Reset).</summary>
    [HttpPost("api/idrac/power")]
    [ProducesResponseType<IdracOverviewResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<IdracOverviewResponse>> Power(IdracPowerRequest request, CancellationToken cancellationToken) =>
        WithRedfishAsync(request, async (client, target) =>
        {
            await IdracRedfish.ResetAsync(client, request.ResetType, cancellationToken);
            await Audit.RecordAsync(new Auditing.AuditEntry("idrac.power", target.HostId, $"{request.ResetType} · {target.Display}"));
            return new IdracOverviewResponse("ready", null, Display(target), null);
        }, cancellationToken);

    /// <summary>
    /// Opens the HTML5 virtual console: one-time console credentials from the iDRAC, then a web session
    /// to the iDRAC whose launch link lands on the console page. Answers like <c>POST /api/web/sessions</c>.
    /// </summary>
    [HttpPost("api/idrac/console")]
    [ProducesResponseType<OpenWebSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OpenWebSessionResponse>> OpenConsole(IdracRequest request, CancellationToken cancellationToken)
    {
        var user = RequestUser.From(User);
        try
        {
            var target = await Resolver.ResolveAsync(request, user, ServiceProtocol.Idrac, DefaultHttpsPort, cancellationToken);
            var uri = WebSessionsController.TargetUri(https: true, target.Address, target.Port);
            var trust = await CheckCertificateAsync(target, request, cancellationToken);
            if (trust.Prompt is { } certificate)
            {
                return WebSessionsController.TrustRequired(uri, certificate);
            }

            string startPath;
            using (var client = new RedfishClient(target, trust.PinnedFingerprint))
            {
                startPath = await IdracRedfish.CreateConsoleLoginAsync(client, target.Username, cancellationToken);
            }

            var session = sessions.TryCreate(user.Id, target.HostId, uri, trust.PinnedFingerprint, WebSessionsController.EmbedderOrigin(Request), startPath);
            if (session is null)
            {
                throw new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {WebSessionStore.MaxSessionsPerUser} open web sessions. Close one and try again.");
            }

            await Audit.RecordAsync(new Auditing.AuditEntry(Auditing.AuditActions.SessionStart, target.HostId, $"iDRAC console · {target.Display}", session.Id));
            return new OpenWebSessionResponse(
                "ready", session.Id, $"{WebProxyMiddleware.LaunchPath}?t={session.LaunchToken}", settings.Value.BaseDomain, uri.ToString(), null);
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    private async Task<ActionResult<IdracOverviewResponse>> WithRedfishAsync(
        IdracRequest request, Func<RedfishClient, SessionTarget, Task<IdracOverviewResponse>> action, CancellationToken cancellationToken)
    {
        try
        {
            var target = await Resolver.ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Idrac, DefaultHttpsPort, cancellationToken);
            var trust = await CheckCertificateAsync(target, request, cancellationToken);
            if (trust.Prompt is { } certificate)
            {
                return new IdracOverviewResponse(
                    "trustRequired", null, Display(target), new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
            }

            using var client = new RedfishClient(target, trust.PinnedFingerprint);
            return await action(client, target);
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    private Task<CertificateTrustResult> CheckCertificateAsync(SessionTarget target, IdracRequest request, CancellationToken cancellationToken)
    {
        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        return CertificateTrust.CheckAsync(
            target.HostId, target.Address, target.Port, target.KeyProtocol, target.KnownKeys, request.TrustFingerprint, User.GetUserId(), HttpContext.RequestServices.GetRequiredService<TrustedKeyStore>(), cancellationToken);
    }

    private Auditing.AuditLog Audit => HttpContext.RequestServices.GetRequiredService<Auditing.AuditLog>();

    private SessionTargetResolver Resolver => HttpContext.RequestServices.GetRequiredService<SessionTargetResolver>();

    private static string Display(SessionTarget target) => WebSessionsController.TargetUri(https: true, target.Address, target.Port).ToString();
}
