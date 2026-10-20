using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
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
    [Audit("host.scan", Detail = "internal")]
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
            var resolver = services.GetRequiredService<SessionTargetResolver>();
            var user = RequestUser.From(User);
            var protocol = (await access.Visible(user).AsNoTracking().Where(h => h.Id == id).Select(h => h.Services).FirstOrDefaultAsync(cancellationToken))
                ?.OrderBy(s => s.Position).ElementAtOrDefault(request.ServiceIndex)?.Protocol;

            if (protocol == ServiceProtocol.Rpc)
            {
                var windowsTarget = await resolver.ResolveAsync(request with { HostId = id }, user, ServiceProtocol.Rpc, 135, cancellationToken, request.Domain);
                SessionChecks.RefuseJumpHost(windowsTarget, "Windows (RPC) scans");
                if (windowsTarget.Password is null)
                {
                    throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
                }

                // System.Management on Windows; Impacket's DCOM client everywhere else.
                var windowsFindings = OperatingSystem.IsWindows()
                    ? await WindowsInternalScan.RunAsync(windowsTarget, cancellationToken)
                    : await ImpacketWmiScan.RunAsync(windowsTarget, cancellationToken);
                return new ScanResponse("ready", windowsFindings, null);
            }

            var target = await resolver
                .ResolveAsync(request with { HostId = id }, user, ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            using var client = await SshCommandConnection.ConnectAsync(
                target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
            var findings = await InternalScan.RunAsync(client, cancellationToken);
            if (findings.Guests.Count > 0)
            {
                findings = findings with { Guests = GuestMatcher.Match(id, findings.Guests, await CandidatesAsync(cancellationToken)) };
            }

            return new ScanResponse("ready", findings, null);
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
    [Audit("host.scan", Detail = "external")]
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
        if (host.JumpHostId is not null)
        {
            return this.SessionFailure(new SessionException(
                SessionErrorCodes.ConnectionFailed, "External scans probe the host's address from the NKeeper server directly, and this host is reached through a jump host. Use an internal scan."));
        }

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

    /// <summary>The hosts the user can see, for matching a hypervisor's guests (and its other nodes).</summary>
    private async Task<List<GuestMatcher.Candidate>> CandidatesAsync(CancellationToken cancellationToken)
    {
        var user = RequestUser.From(User);
        var manageable = await access.ManageableAsync(user, cancellationToken);
        var hosts = await access.Visible(user).AsNoTracking()
            .Select(h => new { h.Id, h.Name, h.Hostname, h.MacAddress, h.IsVirtualMachine, h.HypervisorId, h.VirtualMachineId, h.CpuCores, h.MemoryMiB })
            .ToListAsync(cancellationToken);
        return [.. hosts.Select(h => new GuestMatcher.Candidate(
            h.Id, h.Name, h.Hostname, h.MacAddress, h.IsVirtualMachine, h.HypervisorId, h.VirtualMachineId, manageable?.Contains(h.Id) ?? true, h.CpuCores, h.MemoryMiB))];
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
