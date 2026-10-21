using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Api.Configuration;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

public sealed record OpenWebSessionRequest
{
    public Guid HostId { get; init; }

    [Range(0, 100)]
    public int ServiceIndex { get; init; }

    /// <summary>The certificate fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

public sealed record WebCertificateResponse(string Fingerprint, string Subject, string Issuer, DateTimeOffset Expires);

/// <summary>
/// Either the session is ready (open <see cref="LaunchPath"/> on the session's own origin,
/// <c>nkw-&lt;SessionId&gt;.&lt;BaseDomain or web.&lt;NKeeper host&gt;&gt;</c>), or the target's
/// certificate needs to be trusted first.
/// </summary>
public sealed record OpenWebSessionResponse(
    string Status, string? SessionId, string? LaunchPath, string? BaseDomain, string? Target, WebCertificateResponse? Certificate);

/// <summary>
/// Web (HTTP/HTTPS) sessions: NKeeper itself connects to the host's web interface (or its iDRAC's)
/// and relays it to the browser on an isolated origin, so the browser never contacts the host directly.
/// </summary>
[ApiController]
[Route("api/web/sessions")]
[Authorize]
public sealed class WebSessionsController(HostAccess access, WebSessionStore sessions, IOptions<WebProxySettings> settings) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<OpenWebSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<OpenWebSessionResponse>> Open(OpenWebSessionRequest request, CancellationToken cancellationToken)
    {
        var user = RequestUser.From(User);
        var host = await access.Visible(user).AsNoTracking()
            .Include(h => h.Services)
            .Include(h => h.TrustedKeys)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == request.HostId, cancellationToken);
        if (host is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, detail: "This host no longer exists.");
        }

        var service = host.Services.OrderBy(s => s.Position).ElementAtOrDefault(request.ServiceIndex);
        if (service?.Protocol is not (ServiceProtocol.Http or ServiceProtocol.Https or ServiceProtocol.Idrac or ServiceProtocol.Proxmox))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "The selected service is not a web (HTTP/HTTPS) service. Reload the host and try again.");
        }

        // An iDRAC's web interface is HTTPS on its own address.
        var idrac = service.Protocol == ServiceProtocol.Idrac;
        var https = service.Protocol != ServiceProtocol.Http;
        // An IP address is used over the hostname when both are set, as for every connection.
        var address = service.Address ?? host.IpAddress ?? host.Hostname!;
        var port = service.Port ?? (service.Protocol == ServiceProtocol.Proxmox ? Proxmox.ProxmoxApi.DefaultPort : https ? 443 : 80);
        var target = TargetUri(https, address, port);

        JumpTunnel? tunnel = null;
        string? pinned = null;
        try
        {
            if (host.JumpHostId is { } jumpId)
            {
                var jump = await HttpContext.RequestServices.GetRequiredService<SessionTargetResolver>().ResolveJumpAsync(jumpId, host.JumpCredentialId, user, cancellationToken);
                tunnel = await JumpTunnel.OpenAsync(jump, address, port, cancellationToken);
            }

            if (https)
            {
                var trust = await CertificateTrust.CheckAsync(
                    host.Id, address, port, idrac ? ServiceProtocol.Idrac : ServiceProtocol.Https, host.TrustedKeys, request.TrustFingerprint, user.Id,
                    HttpContext.RequestServices.GetRequiredService<TrustedKeyStore>(), cancellationToken, tunnel);
                if (trust.Prompt is { } certificate)
                {
                    tunnel?.Dispose();
                    return TrustRequired(target, certificate);
                }

                pinned = trust.PinnedFingerprint;
            }
        }
        catch (CertificateChangedException ex)
        {
            tunnel?.Dispose();
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            tunnel?.Dispose();
            return CodedProblem(StatusCodes.Status502BadGateway, ex.Code, ex.Message);
        }

        var embedder = EmbedderOrigin(Request);
        // The session owns the tunnel from here on.
        var session = sessions.TryCreate(user.Id, host.Id, target, pinned, embedder, tunnel: tunnel);
        if (session is null)
        {
            return CodedProblem(
                StatusCodes.Status429TooManyRequests,
                SessionErrorCodes.TooManySessions,
                $"You already have {WebSessionStore.MaxSessionsPerUser} open web sessions. Close one and try again.");
        }

        await HttpContext.RequestServices.GetRequiredService<Auditing.AuditLog>().RecordAsync(
            new Auditing.AuditEntry(Auditing.AuditActions.SessionStart, host.Id, $"{(idrac ? "iDRAC web interface" : "Web")} · {target}", session.Id));
        return new OpenWebSessionResponse(
            "ready", session.Id, $"{WebProxyMiddleware.LaunchPath}?t={session.LaunchToken}", settings.Value.BaseDomain, target.ToString(), null);
    }

    /// <summary>Ends one of the caller's web sessions (its tab in NKeeper was closed).</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Close(string id) => sessions.EndOwned(id, User.GetUserId()) ? NoContent() : NotFound();

    /// <summary>The target's URL, e.g. https://10.0.0.5:8443/ (IPv6 addresses bracketed).</summary>
    internal static Uri TargetUri(bool https, string address, int port) =>
        new UriBuilder(https ? "https" : "http", address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address, port).Uri;

    internal static OpenWebSessionResponse TrustRequired(Uri target, WebCertificate certificate) =>
        new("trustRequired", null, null, null, target.ToString(), new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));

    /// <summary>
    /// The page asking is NKeeper itself (antiforgery and SameSite cookies ensure it), so its
    /// Origin is where the session may be shown in a frame.
    /// </summary>
    internal static string? EmbedderOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https"
            ? origin.GetLeftPart(UriPartial.Authority)
            : null;

    private ObjectResult CodedProblem(int status, string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
