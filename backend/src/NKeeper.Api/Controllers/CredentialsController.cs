using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory;
using NKeeper.Api.Security;

namespace NKeeper.Api.Controllers;

/// <summary>The shared credential library. Responses never contain secret values.</summary>
[ApiController]
[Route("api/credentials")]
[Authorize]
public sealed class CredentialsController(CredentialManager credentials) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<CredentialResponse>>(StatusCodes.Status200OK)]
    public Task<List<CredentialResponse>> List(CancellationToken cancellationToken) => credentials.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CredentialResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CredentialResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await credentials.GetAsync(id, cancellationToken) is { } credential ? credential : NotFound();

    [HttpPost]
    [ProducesResponseType<CredentialResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CredentialResponse>> Create(CredentialRequest request, CancellationToken cancellationToken)
    {
        var credential = await credentials.CreateAsync(request, User.GetUserId(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = credential.Id }, credential);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CredentialResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CredentialResponse>> Update(Guid id, CredentialRequest request, CancellationToken cancellationToken) =>
        await credentials.UpdateAsync(id, request, User.GetUserId(), cancellationToken) is { } credential ? credential : NotFound();

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await credentials.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
