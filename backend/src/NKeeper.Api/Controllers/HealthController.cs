using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Sdk.Access;
using NKeeper.Api.Auditing;
using NKeeper.Sdk.Auditing;
using NKeeper.Api.Health;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Controllers;

/// <param name="Address">The address checked when it isn't the host's (an iDRAC's); null otherwise.</param>
/// <param name="DownSinceUtc">When it went down, in the current run of failed checks.</param>
public sealed record ServiceCheckResponse(
    Guid HostId,
    ServiceProtocol Protocol,
    int Port,
    string? Address,
    bool IsUp,
    int? LatencyMs,
    string? Error,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? DownSinceUtc,
    DateTimeOffset? CertificateExpiresUtc,
    string? CertificateSubject);

public sealed record WakeResponse(IReadOnlyList<string> SentTo);

/// <summary>Service health (for the hosts the user can see), checking a host now, and Wake-on-LAN.</summary>
[ApiController]
[Authorize]
public sealed class HealthController(HostAccess access, NKeeperDbContext db) : ControllerBase
{
    [HttpGet("api/health")]
    [ProducesResponseType<List<ServiceCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<List<ServiceCheckResponse>> List(CancellationToken cancellationToken)
    {
        var visible = access.Visible(RequestUser.From(User)).Select(h => h.Id);
        var checks = await db.ServiceChecks.AsNoTracking().Where(c => visible.Contains(c.HostId)).ToListAsync(cancellationToken);
        return [.. checks.Select(ToResponse)];
    }

    /// <summary>Checks one host's services now, rather than waiting for the next round.</summary>
    [HttpPost("api/health/hosts/{id:guid}/check")]
    [ProducesResponseType<List<ServiceCheckResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ServiceCheckResponse>>> Check(Guid id, CancellationToken cancellationToken)
    {
        if (!await access.Visible(RequestUser.From(User)).AnyAsync(h => h.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var checker = HttpContext.RequestServices.GetRequiredService<HostHealthChecker>();
        return (await checker.CheckHostAsync(id, cancellationToken)).Select(ToResponse).ToList();
    }

    /// <summary>Sends a Wake-on-LAN packet to the host's MAC address, from the NKeeper server.</summary>
    [HttpPost("api/hosts/{id:guid}/wake")]
    [ProducesResponseType<WakeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WakeResponse>> Wake(Guid id, CancellationToken cancellationToken)
    {
        var host = await access.Visible(RequestUser.From(User)).AsNoTracking()
            .Where(h => h.Id == id).Select(h => new { h.Name, h.MacAddress, h.IpAddress }).FirstOrDefaultAsync(cancellationToken);
        if (host is null)
        {
            return NotFound();
        }

        if (host.MacAddress is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This host has no MAC address. Add one (or scan the host) to wake it.");
        }

        var sent = await WakeOnLan.SendAsync(host.MacAddress, host.IpAddress, cancellationToken);
        await HttpContext.RequestServices.GetRequiredService<AuditLog>().RecordAsync(
            new AuditEntry("host.wake", id, $"{host.MacAddress} to {string.Join(", ", sent)}", HostName: host.Name));
        return new WakeResponse(sent);
    }

    private static ServiceCheckResponse ToResponse(ServiceCheck c) => new(
        c.HostId, c.Protocol, c.Port, c.Address.Length > 0 ? c.Address : null, c.IsUp, c.LatencyMs, c.Error, c.CheckedAtUtc, c.DownSinceUtc,
        c.CertificateExpiresUtc, c.CertificateSubject);
}
