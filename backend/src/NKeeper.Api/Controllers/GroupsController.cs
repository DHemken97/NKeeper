using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory;

namespace NKeeper.Api.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public sealed class GroupsController(GroupManager groups) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<GroupResponse>>(StatusCodes.Status200OK)]
    public Task<List<GroupResponse>> List(CancellationToken cancellationToken) => groups.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<GroupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GroupResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await groups.GetAsync(id, cancellationToken) is { } group ? group : NotFound();

    [HttpPost]
    [ProducesResponseType<GroupResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GroupResponse>> Create(GroupRequest request, CancellationToken cancellationToken)
    {
        var group = await groups.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = group.Id }, group);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<GroupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GroupResponse>> Update(Guid id, GroupRequest request, CancellationToken cancellationToken) =>
        await groups.UpdateAsync(id, request, cancellationToken) is { } group ? group : NotFound();

    /// <summary>Deletes the group. Its hosts are kept and become ungrouped.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await groups.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}

[ApiController]
[Route("api/tags")]
[Authorize]
public sealed class TagsController(GroupManager groups) : ControllerBase
{
    /// <summary>Tags currently in use, with how many hosts carry each.</summary>
    [HttpGet]
    [ProducesResponseType<List<TagResponse>>(StatusCodes.Status200OK)]
    public Task<List<TagResponse>> List(CancellationToken cancellationToken) => groups.ListTagsAsync(cancellationToken);
}
