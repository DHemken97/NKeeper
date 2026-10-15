using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory;

public sealed class HostManager(NKeeperDbContext db, TimeProvider timeProvider)
{
    private const string LikeEscape = "\\";

    private static readonly Expression<Func<Host, HostSummaryResponse>> SummaryProjection = h => new HostSummaryResponse(
        h.Id,
        h.Name,
        h.Hostname,
        h.IpAddress,
        h.MacAddress,
        h.Group == null ? null : new GroupReference(h.Group.Id, h.Group.Name),
        h.Tags.OrderBy(t => t.NormalizedName).Select(t => t.Name).ToList(),
        h.Services.OrderBy(s => s.Position).Select(s => new HostServiceResponse(s.Protocol, s.Port, s.Label)).ToList(),
        h.Credentials.Count,
        h.UpdatedAtUtc);

    private static readonly Expression<Func<Host, HostResponse>> DetailProjection = h => new HostResponse(
        h.Id,
        h.Name,
        h.Hostname,
        h.IpAddress,
        h.MacAddress,
        h.Description,
        h.Group == null ? null : new GroupReference(h.Group.Id, h.Group.Name),
        h.Tags.OrderBy(t => t.NormalizedName).Select(t => t.Name).ToList(),
        h.Services.OrderBy(s => s.Position).Select(s => new HostServiceResponse(s.Protocol, s.Port, s.Label)).ToList(),
        h.Credentials.OrderBy(c => c.Name.ToUpper()).Select(c => new CredentialReference(c.Id, c.Name, c.Username, c.Domain)).ToList(),
        h.CreatedAtUtc,
        h.UpdatedAtUtc,
        h.Version);

    public async Task<PagedResponse<HostSummaryResponse>> ListAsync(HostListQuery query, CancellationToken cancellationToken)
    {
        var hosts = db.Hosts.AsNoTracking();

        foreach (var term in (query.Search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = $"%{EscapeLike(term)}%";
            hosts = hosts.Where(h =>
                EF.Functions.ILike(h.Name, pattern, LikeEscape)
                || (h.Hostname != null && EF.Functions.ILike(h.Hostname, pattern, LikeEscape))
                || (h.IpAddress != null && EF.Functions.ILike(h.IpAddress, pattern, LikeEscape))
                || (h.MacAddress != null && EF.Functions.ILike(h.MacAddress, pattern, LikeEscape))
                || (h.Description != null && EF.Functions.ILike(h.Description, pattern, LikeEscape))
                || (h.Group != null && EF.Functions.ILike(h.Group.Name, pattern, LikeEscape))
                || h.Tags.Any(t => EF.Functions.ILike(t.Name, pattern, LikeEscape)));
        }

        if (query.GroupId is { } groupId)
        {
            hosts = hosts.Where(h => h.GroupId == groupId);
        }
        else if (query.Ungrouped)
        {
            hosts = hosts.Where(h => h.GroupId == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var normalizedTag = Tag.Normalize(query.Tag);
            hosts = hosts.Where(h => h.Tags.Any(t => t.NormalizedName == normalizedTag));
        }

        if (query.Protocol is { } protocol)
        {
            hosts = hosts.Where(h => h.Services.Any(s => s.Protocol == protocol));
        }

        var totalCount = await hosts.CountAsync(cancellationToken);
        var items = await Sort(hosts, query.Sort, query.Direction == SortDirection.Desc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken);

        return new PagedResponse<HostSummaryResponse>(items, totalCount, query.Page, query.PageSize);
    }

    public Task<HostResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Hosts.AsNoTracking().Where(h => h.Id == id).Select(DetailProjection).FirstOrDefaultAsync(cancellationToken);

    public async Task<HostResponse> CreateAsync(HostRequest request, Guid userId, CancellationToken cancellationToken)
    {
        var host = new Host { CreatedAtUtc = timeProvider.GetUtcNow(), CreatedByUserId = userId };
        await ApplyAsync(host, request, userId, cancellationToken);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken);
        await RemoveUnusedTagsAsync(cancellationToken);
        return (await GetAsync(host.Id, cancellationToken))!;
    }

    /// <returns>The updated host, or null when it does not exist.</returns>
    /// <exception cref="DbUpdateConcurrencyException">The host changed since <see cref="HostRequest.Version"/>.</exception>
    public async Task<HostResponse?> UpdateAsync(Guid id, HostRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (request.Version is not { } version)
        {
            throw new InventoryValidationException("version", "The version of the host being edited is required.");
        }

        var host = await db.Hosts
            .Include(h => h.Services)
            .Include(h => h.Tags)
            .Include(h => h.Credentials)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        db.Entry(host).Property(h => h.Version).OriginalValue = version;
        await ApplyAsync(host, request, userId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RemoveUnusedTagsAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // Services and tag/credential links are removed by cascading foreign keys.
        var deleted = await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
        if (deleted)
        {
            await RemoveUnusedTagsAsync(cancellationToken);
        }

        return deleted;
    }

    private async Task ApplyAsync(Host host, HostRequest request, Guid userId, CancellationToken cancellationToken)
    {
        host.Name = request.Name.Trim();
        host.Hostname = InventoryText.TrimToNull(request.Hostname);
        host.IpAddress = NetworkAddress.TryNormalizeIp(request.IpAddress ?? string.Empty, out var ip) ? ip : null;
        host.MacAddress = NetworkAddress.TryNormalizeMac(request.MacAddress ?? string.Empty, out var mac) ? mac : null;
        host.Description = InventoryText.TrimToNull(request.Description);
        host.UpdatedAtUtc = timeProvider.GetUtcNow();
        host.UpdatedByUserId = userId;

        if (request.GroupId is { } groupId && !await db.HostGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new InventoryValidationException("groupId", "The selected group no longer exists.");
        }

        host.GroupId = request.GroupId;

        host.Services.Clear();
        host.Services.AddRange(request.Services.Select((s, index) => new HostService
        {
            Protocol = s.Protocol!.Value,
            Port = s.Port,
            Label = InventoryText.TrimToNull(s.Label),
            Position = index,
        }));

        var tags = await ResolveTagsAsync(request.Tags, cancellationToken);
        host.Tags.Clear();
        host.Tags.AddRange(tags);

        var credentialIds = request.CredentialIds.Distinct().ToList();
        var credentials = await db.Credentials.Where(c => credentialIds.Contains(c.Id)).ToListAsync(cancellationToken);
        if (credentials.Count != credentialIds.Count)
        {
            throw new InventoryValidationException("credentialIds", "One or more selected credentials no longer exist.");
        }

        host.Credentials.Clear();
        host.Credentials.AddRange(credentials);
    }

    /// <summary>Finds existing tags case-insensitively and creates the missing ones.</summary>
    private async Task<List<Tag>> ResolveTagsAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var requested = names
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .DistinctBy(Tag.Normalize)
            .ToList();
        var normalized = requested.Select(Tag.Normalize).ToList();

        var existing = await db.Tags.Where(t => normalized.Contains(t.NormalizedName)).ToListAsync(cancellationToken);
        var created = requested
            .Where(name => existing.All(t => t.NormalizedName != Tag.Normalize(name)))
            .Select(name => new Tag { Name = name, NormalizedName = Tag.Normalize(name) });

        return [.. existing, .. created];
    }

    private Task<int> RemoveUnusedTagsAsync(CancellationToken cancellationToken) =>
        db.Tags.Where(t => !t.Hosts.Any()).ExecuteDeleteAsync(cancellationToken);

    private static IQueryable<Host> Sort(IQueryable<Host> hosts, HostSortField field, bool descending)
    {
        var ordered = field switch
        {
            HostSortField.Address => OrderBy(hosts, h => (h.Hostname ?? h.IpAddress)!.ToUpper(), descending),
            HostSortField.Group => OrderBy(hosts, h => h.Group != null ? h.Group.NormalizedName : null, descending),
            HostSortField.Updated => OrderBy(hosts, h => h.UpdatedAtUtc, descending),
            _ => OrderBy(hosts, h => h.Name.ToUpper(), descending),
        };

        // Stable secondary ordering keeps paging deterministic.
        return ordered.ThenBy(h => h.Name.ToUpper()).ThenBy(h => h.Id);
    }

    private static IOrderedQueryable<Host> OrderBy<TKey>(IQueryable<Host> hosts, Expression<Func<Host, TKey>> key, bool descending) =>
        descending ? hosts.OrderByDescending(key) : hosts.OrderBy(key);

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

}
