using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Infrastructure.Identity;
using NKeeper.Api.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory;
using NKeeper.Api.Inventory.Import;

namespace NKeeper.Api.Controllers;

[ApiController]
[Route("api/hosts")]
[Authorize]
public sealed class HostsController(HostManager hosts) : ControllerBase
{
    /// <summary>
    /// Every host the user can see, with tags, services and credentials (never secrets), ordered
    /// by name. Clients search, group and sort it locally.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<List<HostResponse>>(StatusCodes.Status200OK)]
    public Task<List<HostResponse>> List(CancellationToken cancellationToken) => hosts.ListAsync(RequestUser.From(User), cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<HostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await hosts.GetAsync(id, RequestUser.From(User), cancellationToken) is { } host ? host : NotFound();

    /// <summary>Adds a host. Administrators only.</summary>
    [HttpPost]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<HostResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HostResponse>> Create(HostRequest request, CancellationToken cancellationToken)
    {
        var host = await hosts.CreateAsync(request, RequestUser.From(User), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = host.Id }, host);
    }

    /// <summary>
    /// Imports an mRemoteNG connection file: adds, updates and/or deletes hosts, matched by name.
    /// Send <c>dryRun</c> first to see what would change. Administrators only.
    /// </summary>
    [HttpPost("import")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [RequestSizeLimit(HostImportRequest.MaxContentLength * 2)]
    [ProducesResponseType<HostImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<HostImportResponse> Import(HostImportRequest request, [FromServices] HostImporter importer, CancellationToken cancellationToken) =>
        importer.ImportAsync(request, RequestUser.From(User), cancellationToken);

    /// <summary>Replaces a host. Send the <c>version</c> you read; a stale version returns 409.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<HostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostResponse>> Update(Guid id, HostRequest request, CancellationToken cancellationToken) =>
        await hosts.UpdateAsync(id, request, RequestUser.From(User), cancellationToken) is { } host ? host : NotFound();

    /// <summary>Forgets the host's trusted SSH host keys and RDP certificates; the next connection asks again.</summary>
    [HttpDelete("{id:guid}/trusted-keys")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForgetTrustedKeys(Guid id, CancellationToken cancellationToken) =>
        await hosts.ForgetTrustedKeysAsync(id, RequestUser.From(User), cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await hosts.DeleteAsync(id, RequestUser.From(User), cancellationToken) ? NoContent() : NotFound();
}
