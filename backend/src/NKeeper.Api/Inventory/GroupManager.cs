using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Inventory;

public sealed class GroupManager(NKeeperDbContext db, TimeProvider timeProvider)
{
    public Task<List<GroupResponse>> ListAsync(CancellationToken cancellationToken) =>
        db.HostGroups.AsNoTracking()
            .OrderBy(g => g.NormalizedName)
            .Select(g => new GroupResponse(g.Id, g.Name, g.Description, g.Hosts.Count))
            .ToListAsync(cancellationToken);

    public Task<GroupResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.HostGroups.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new GroupResponse(g.Id, g.Name, g.Description, g.Hosts.Count))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<GroupResponse> CreateAsync(GroupRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var group = new HostGroup { CreatedAtUtc = now };
        await ApplyAsync(group, request, cancellationToken);
        db.HostGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(group.Id, cancellationToken))!;
    }

    public async Task<GroupResponse?> UpdateAsync(Guid id, GroupRequest request, CancellationToken cancellationToken)
    {
        var group = await db.HostGroups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return null;
        }

        await ApplyAsync(group, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Deletes the group; its hosts become ungrouped (they are not deleted).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.HostGroups.Where(g => g.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    public Task<List<TagResponse>> ListTagsAsync(CancellationToken cancellationToken) =>
        db.Tags.AsNoTracking()
            .OrderBy(t => t.NormalizedName)
            .Select(t => new TagResponse(t.Name, t.Hosts.Count))
            .ToListAsync(cancellationToken);

    private async Task ApplyAsync(HostGroup group, GroupRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var normalized = name.ToUpperInvariant();
        if (await db.HostGroups.AnyAsync(g => g.NormalizedName == normalized && g.Id != group.Id, cancellationToken))
        {
            throw new InventoryValidationException("name", "A group with this name already exists.");
        }

        group.Name = name;
        group.NormalizedName = normalized;
        group.Description = InventoryText.TrimToNull(request.Description);
        group.UpdatedAtUtc = timeProvider.GetUtcNow();
    }
}
