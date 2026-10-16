using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

/// <summary>
/// Inventory scans (Tasks ▸ Scan): they gather facts about a host for the user to review. Nothing
/// is saved here; the client applies the chosen findings with an ordinary host update. Scanning
/// needs Manage access to the host.
/// </summary>
[ApiController]
[Route("api/hosts/{id:guid}/scan")]
[Authorize]
public sealed class HostScanController(HostAccess access) : ControllerBase
{
    private const int DefaultSshPort = 22;

    /// <summary>
    /// Internal scan: logs in over SSH and reads the host's own state (hostname, OS, MAC address,
    /// listening ports, local users). On the network it is a single SSH connection.
    /// </summary>
    [HttpPost("internal")]
    [ProducesResponseType<ScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanResponse>> Internal(Guid id, InternalScanRequest request, CancellationToken cancellationToken)
    {
        if (await RequireManageAsync(id, cancellationToken) is { } denied)
        {
            return denied;
        }

        var services = HttpContext.RequestServices;
        try
        {
            var target = await services.GetRequiredService<SessionTargetResolver>()
                .ResolveAsync(request with { HostId = id }, RequestUser.From(User), ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            using var client = await SshCommandConnection.ConnectAsync(
                target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
            return new ScanResponse("ready", await InternalScan.RunAsync(client, cancellationToken), null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new ScanResponse("trustRequired", null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    /// <summary>External scan: probes common TCP ports on the host's address from the NKeeper server.</summary>
    [HttpPost("external")]
    [ProducesResponseType<ScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanResponse>> External(Guid id, CancellationToken cancellationToken)
    {
        if (await RequireManageAsync(id, cancellationToken) is { } denied)
        {
            return denied;
        }

        var host = await access.Manageable(RequestUser.From(User)).AsNoTracking().Include(h => h.Services).FirstAsync(h => h.Id == id, cancellationToken);
        try
        {
            var scanner = HttpContext.RequestServices.GetRequiredService<ExternalScan>();
            return new ScanResponse("ready", await scanner.ScanAsync(host, cancellationToken), null);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    /// <returns>Null when the user may manage the host; otherwise the response to send.</returns>
    private async Task<ActionResult?> RequireManageAsync(Guid id, CancellationToken cancellationToken) =>
        await access.GetPermissionAsync(id, RequestUser.From(User), cancellationToken) switch
        {
            HostPermission.Manage => null,
            HostPermission.None => NotFound(),
            _ => throw new HostAccessDeniedException(),
        };
}
