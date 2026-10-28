using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Sdk.Access;
using NKeeper.Sdk.Auditing;
using NKeeper.Sdk.Sessions;
using NKeeper.Sdk.Security;
using NKeeper.Sdk.Web;

namespace NKeeper.Plugins.Idrac;

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
public sealed class IdracController(IWebSessions webSessions) : ControllerBase
{
    private const int DefaultHttpsPort = 443;

    [HttpPost("overview")]
    [ProducesResponseType<IdracOverviewResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<IdracOverviewResponse>> Overview(IdracRequest request, CancellationToken cancellationToken) =>
        WithRedfishAsync(request, async (client, target) =>
            new IdracOverviewResponse("ready", await IdracRedfish.GetOverviewAsync(client, cancellationToken), Display(target), null),
            cancellationToken);

    /// <summary>Presses one of the server's power buttons (Redfish ComputerSystem.Reset).</summary>
    [HttpPost("power")]
    [ProducesResponseType<IdracOverviewResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<IdracOverviewResponse>> Power(IdracPowerRequest request, CancellationToken cancellationToken) =>
        WithRedfishAsync(request, async (client, target) =>
        {
            await IdracRedfish.ResetAsync(client, request.ResetType, cancellationToken);
            await Audit.RecordAsync(new AuditEntry("idrac.power", target.HostId, $"{request.ResetType} · {target.Display}"));
            return new IdracOverviewResponse("ready", null, Display(target), null);
        }, cancellationToken);

    /// <summary>
    /// Opens the HTML5 virtual console: one-time console credentials from the iDRAC, then a web session
    /// to the iDRAC whose launch link lands on the console page. Answers like <c>POST /api/web/sessions</c>.
    /// </summary>
    [HttpPost("console")]
    [ProducesResponseType<OpenWebSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OpenWebSessionResponse>> OpenConsole(IdracRequest request, CancellationToken cancellationToken)
    {
        var user = RequestUser.From(User);
        JumpTunnel? tunnel = null;
        try
        {
            var target = await Resolver.ResolveAsync(request, user, ServiceProtocol.Idrac, DefaultHttpsPort, cancellationToken);
            var uri = IWebSessions.TargetUri(https: true, target.Address, target.Port);
            tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, target.Port, cancellationToken);
            var trust = await CheckCertificateAsync(target, request, tunnel, cancellationToken);
            if (trust.Prompt is { } certificate)
            {
                tunnel.Dispose();
                return OpenWebSessionResponse.TrustRequired(uri, certificate);
            }

            string startPath;
            using (var client = new RedfishClient(target, trust.PinnedFingerprint, tunnel))
            {
                startPath = await IdracRedfish.CreateConsoleLoginAsync(client, target.Username, cancellationToken);
            }

            // The console's web session owns the tunnel from here on.
            var owned = tunnel;
            tunnel = null;
            return await webSessions.OpenAsync(HttpContext, target.HostId, uri, trust.PinnedFingerprint, startPath, owned, $"iDRAC console · {target.Display}");
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
        finally
        {
            tunnel?.Dispose();
        }
    }

    private async Task<ActionResult<IdracOverviewResponse>> WithRedfishAsync(
        IdracRequest request, Func<RedfishClient, SessionTarget, Task<IdracOverviewResponse>> action, CancellationToken cancellationToken)
    {
        try
        {
            var target = await Resolver.ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Idrac, DefaultHttpsPort, cancellationToken);
            using var tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, target.Port, cancellationToken);
            var trust = await CheckCertificateAsync(target, request, tunnel, cancellationToken);
            if (trust.Prompt is { } certificate)
            {
                return new IdracOverviewResponse(
                    "trustRequired", null, Display(target), new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
            }

            using var client = new RedfishClient(target, trust.PinnedFingerprint, tunnel);
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

    private Task<CertificateTrustResult> CheckCertificateAsync(SessionTarget target, IdracRequest request, JumpTunnel tunnel, CancellationToken cancellationToken)
    {
        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        return CertificateTrust.CheckAsync(
            target.HostId, target.Address, target.Port, target.KeyProtocol, target.KnownKeys, request.TrustFingerprint, User.GetUserId(), HttpContext.RequestServices.GetRequiredService<TrustedKeyStore>(), cancellationToken, tunnel);
    }

    private AuditLog Audit => HttpContext.RequestServices.GetRequiredService<AuditLog>();

    private SessionTargetResolver Resolver => HttpContext.RequestServices.GetRequiredService<SessionTargetResolver>();

    private static string Display(SessionTarget target) => IWebSessions.TargetUri(https: true, target.Address, target.Port).ToString();
}
