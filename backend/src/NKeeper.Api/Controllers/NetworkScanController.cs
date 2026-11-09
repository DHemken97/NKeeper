using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Controllers;

/// <summary>
/// Network scans (Hosts ▸ Scan network): the server sweeps a whole subnet for machines and learns
/// what it can about each (names, MAC address, open ports, what its services say). Nothing is saved:
/// the client adds the machines the user picks as ordinary hosts. This is loud on the network, so it
/// is for administrators only, must be confirmed, and is recorded in the audit trail.
/// </summary>
[ApiController]
[Route("api/network-scan")]
[Authorize(Roles = SystemRoles.Administrator)]
public sealed class NetworkScanController(NetworkScans scans, HostAccess access, AuditLog audit) : ControllerBase
{
    /// <summary>The networks the NKeeper server is on, suggested as targets.</summary>
    [HttpGet("networks")]
    public IEnumerable<LocalNetworkResponse> Networks() =>
        Subnet.Local().Select(l => new LocalNetworkResponse(l.Subnet.ToString(), l.Interface, l.Address));

    /// <summary>The user's most recent scan, running or finished; 204 when there's none.</summary>
    [HttpGet("latest")]
    [ProducesResponseType<NetworkScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<NetworkScanResponse> Latest() => scans.Latest(User.GetUserId()) is { } job ? job.View() : NoContent();

    /// <summary>Starts sweeping the subnet. Returns at once; poll the scan for its progress.</summary>
    [HttpPost]
    [ProducesResponseType<NetworkScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NetworkScanResponse>> Start(NetworkScanRequest request, CancellationToken cancellationToken)
    {
        if (!request.Acknowledged)
        {
            ModelState.AddModelError(nameof(request.Acknowledged), "Confirm that you understand the scan is loud and may set off security tools.");
            return ValidationProblem(ModelState);
        }

        if (!Subnet.TryParse(request.Subnet, out var subnet, out var error))
        {
            ModelState.AddModelError(nameof(request.Subnet), error);
            return ValidationProblem(ModelState);
        }

        var user = RequestUser.From(User);
        var hosts = await access.Visible(user).AsNoTracking()
            .Select(h => new { h.Id, h.Name, h.Hostname, h.IpAddress, h.MacAddress })
            .ToListAsync(cancellationToken);
        var candidates = hosts
            .Select(h => new GuestMatcher.Candidate(h.Id, h.Name, h.Hostname, h.MacAddress, false, null, null, true, null, null, h.IpAddress))
            .ToList();

        var job = scans.Start(user.Id, subnet, candidates);
        if (job is null)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A network scan is already running",
                detail: "Only one network scan runs at a time. Wait for it to finish, or cancel it.");
        }

        await audit.RecordAsync(new AuditEntry("network.scan", Detail: $"{subnet} · {subnet.Count:N0} addresses"));
        return job.View();
    }

    [HttpGet("{id}")]
    [ProducesResponseType<NetworkScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NetworkScanResponse> Get(string id) => scans.Find(id, User.GetUserId()) is { } job ? job.View() : NotFound();

    /// <summary>Stops the scan; what it found so far stays.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Cancel(string id)
    {
        if (scans.Find(id, User.GetUserId()) is not { } job)
        {
            return NotFound();
        }

        if (job.FinishedAtUtc is null)
        {
            job.Cancel();
        }

        return NoContent();
    }
}
