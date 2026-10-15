using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory;
using NKeeper.Api.Security;

namespace NKeeper.Api.Controllers;

[ApiController]
[Route("api/hosts")]
[Authorize]
public sealed class HostsController(HostManager hosts) : ControllerBase
{
    /// <summary>Searches, filters, sorts and pages the host inventory.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<HostSummaryResponse>>(StatusCodes.Status200OK)]
    public Task<PagedResponse<HostSummaryResponse>> List([FromQuery] HostListQuery query, CancellationToken cancellationToken) =>
        hosts.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<HostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await hosts.GetAsync(id, cancellationToken) is { } host ? host : NotFound();

    [HttpPost]
    [ProducesResponseType<HostResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HostResponse>> Create(HostRequest request, CancellationToken cancellationToken)
    {
        var host = await hosts.CreateAsync(request, User.GetUserId(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = host.Id }, host);
    }

    /// <summary>Replaces a host. Send the <c>version</c> you read; a stale version returns 409.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<HostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostResponse>> Update(Guid id, HostRequest request, CancellationToken cancellationToken) =>
        await hosts.UpdateAsync(id, request, User.GetUserId(), cancellationToken) is { } host ? host : NotFound();

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await hosts.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
