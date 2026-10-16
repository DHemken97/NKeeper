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
/// Web (HTTP/HTTPS) sessions: NKeeper itself connects to the host's web interface and relays it
/// to the browser on an isolated origin, so the browser never contacts the host directly.
/// </summary>
[ApiController]
[Route("api/web/sessions")]
[Authorize]
public sealed class WebSessionsController(HostAccess access, WebSessionStore sessions, IOptions<WebProxySettings> settings) : ControllerBase
{
    public const string CertificateAlgorithm = "x509";

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
        if (service?.Protocol is not (ServiceProtocol.Http or ServiceProtocol.Https))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "The selected service is not a web (HTTP/HTTPS) service. Reload the host and try again.");
        }

        var https = service.Protocol == ServiceProtocol.Https;
        var address = host.Hostname ?? host.IpAddress!;
        var port = service.Port ?? (https ? 443 : 80);
        var target = new UriBuilder(https ? "https" : "http", address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address, port).Uri;

        string? pinned = null;
        if (https)
        {
            WebCertificate certificate;
            try
            {
                certificate = await WebTargetProbe.ProbeAsync(address, port, cancellationToken);
            }
            catch (SessionException ex)
            {
                return CodedProblem(StatusCodes.Status502BadGateway, ex.Code, ex.Message);
            }

            if (!certificate.IsValid)
            {
                var trustedKeys = HttpContext.RequestServices.GetRequiredService<TrustedKeyStore>();
                var known = host.TrustedKeys.FirstOrDefault(k => k.Protocol == ServiceProtocol.Https && k.Port == port && k.Algorithm == CertificateAlgorithm);
                if (known is not null && !string.Equals(known.Fingerprint, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    trustedKeys.LogChanged(host.Id, port, ServiceProtocol.Https, known.Fingerprint, certificate.Fingerprint);
                    var problem = CodedProblem(
                        StatusCodes.Status409Conflict,
                        SessionErrorCodes.HostKeyChanged,
                        TrustedKeyStore.ChangedMessage("TLS certificate", address, port));
                    ((ProblemDetails)problem.Value!).Extensions["fingerprint"] = certificate.Fingerprint;
                    ((ProblemDetails)problem.Value!).Extensions["trustedFingerprint"] = known.Fingerprint;
                    return problem;
                }

                if (known is null)
                {
                    if (!string.Equals(request.TrustFingerprint, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return new OpenWebSessionResponse(
                            "trustRequired", null, null, null, target.ToString(),
                            new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
                    }

                    await trustedKeys.TrustAsync(host.Id, port, ServiceProtocol.Https, CertificateAlgorithm, certificate.Fingerprint, user.Id);
                }

                pinned = certificate.Fingerprint;
            }
        }

        // The page asking is NKeeper itself (antiforgery and SameSite cookies ensure it), so its
        // Origin is where the session may be shown in a frame.
        var embedder = Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https"
            ? origin.GetLeftPart(UriPartial.Authority)
            : null;
        var session = sessions.TryCreate(user.Id, host.Id, target, pinned, embedder);
        if (session is null)
        {
            return CodedProblem(
                StatusCodes.Status429TooManyRequests,
                SessionErrorCodes.TooManySessions,
                $"You already have {WebSessionStore.MaxSessionsPerUser} open web sessions. Close one and try again.");
        }

        return new OpenWebSessionResponse(
            "ready", session.Id, $"{WebProxyMiddleware.LaunchPath}?t={session.LaunchToken}", settings.Value.BaseDomain, target.ToString(), null);
    }

    /// <summary>Ends one of the caller's web sessions (its tab in NKeeper was closed).</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Close(string id) => sessions.EndOwned(id, User.GetUserId()) ? NoContent() : NotFound();

    private ObjectResult CodedProblem(int status, string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
