using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory;

/// <summary>
/// Manages hosts together with the tags, services and credentials they own.
/// Credential secrets go in encrypted and never come back out.
/// </summary>
public sealed class HostManager(NKeeperDbContext db, CredentialProtector protector, TimeProvider timeProvider)
{
    private static readonly Expression<Func<Host, HostResponse>> Projection = h => new HostResponse(
        h.Id,
        h.Name,
        h.Hostname,
        h.IpAddress,
        h.MacAddress,
        h.Description,
        h.Tags
            .OrderBy(t => t.Name.ToUpper()).ThenBy(t => t.Value.ToUpper())
            .Select(t => new HostTagResponse(t.Name, t.Value == "" ? null : t.Value))
            .ToList(),
        h.Services.OrderBy(s => s.Position).Select(s => new HostServiceResponse(s.Protocol, s.Port, s.Label)).ToList(),
        h.Credentials
            .OrderBy(c => c.Position)
            .Select(c => new HostCredentialResponse(c.Id, c.Label, c.Username, c.Domain, c.Notes, c.ProtectedPassword != null, c.ProtectedPrivateKey != null))
            .ToList(),
        h.SshHostKeys
            .OrderBy(k => k.Port).ThenBy(k => k.Algorithm)
            .Select(k => new SshHostKeyResponse(k.Port, k.Algorithm, k.Fingerprint, k.TrustedAtUtc))
            .ToList(),
        h.CreatedAtUtc,
        h.UpdatedAtUtc,
        h.Version);

    /// <summary>
    /// The whole inventory, ordered by name. The client searches, groups and sorts it locally,
    /// which keeps the connection tree instant (inventories are hundreds to a few thousand hosts).
    /// </summary>
    public Task<List<HostResponse>> ListAsync(CancellationToken cancellationToken) =>
        db.Hosts.AsNoTracking()
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
            .Select(Projection)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public Task<HostResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Hosts.AsNoTracking().Where(h => h.Id == id).Select(Projection).AsSplitQuery().FirstOrDefaultAsync(cancellationToken);

    public async Task<HostResponse> CreateAsync(HostRequest request, Guid userId, CancellationToken cancellationToken)
    {
        var host = new Host { CreatedAtUtc = timeProvider.GetUtcNow(), CreatedByUserId = userId };
        Apply(host, request, userId);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken);
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
        var previousAddress = (host.Hostname, host.IpAddress);
        Apply(host, request, userId);
        await db.SaveChangesAsync(cancellationToken);

        // A new address may be a different machine: its SSH keys must be confirmed again.
        if ((host.Hostname, host.IpAddress) != previousAddress)
        {
            await db.HostSshKeys.Where(k => k.HostId == id).ExecuteDeleteAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Forgets every trusted SSH host key, so the next connection asks again.</summary>
    /// <returns>False when the host does not exist.</returns>
    public async Task<bool> ForgetSshHostKeysAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Hosts.AnyAsync(h => h.Id == id, cancellationToken))
        {
            return false;
        }

        await db.HostSshKeys.Where(k => k.HostId == id).ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes the host; its tags, services and credentials go with it (cascade).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private void Apply(Host host, HostRequest request, Guid userId)
    {
        host.Name = request.Name.Trim();
        host.Hostname = InventoryText.TrimToNull(request.Hostname);
        host.IpAddress = NetworkAddress.TryNormalizeIp(request.IpAddress ?? string.Empty, out var ip) ? ip : null;
        host.MacAddress = NetworkAddress.TryNormalizeMac(request.MacAddress ?? string.Empty, out var mac) ? mac : null;
        host.Description = InventoryText.TrimToNull(request.Description);
        host.UpdatedAtUtc = timeProvider.GetUtcNow();
        host.UpdatedByUserId = userId;

        ApplyServices(host, request.Services);
        ApplyTags(host, request.Tags);
        ApplyCredentials(host, request.Credentials);
    }

    private static void ApplyServices(Host host, IEnumerable<HostServiceRequest> services)
    {
        host.Services.Clear();
        host.Services.AddRange(services.Select((s, index) => new HostService
        {
            Protocol = s.Protocol!.Value,
            Port = s.Port,
            Label = InventoryText.TrimToNull(s.Label),
            Position = index,
        }));
    }

    /// <summary>Replaces the tags, dropping case-insensitive duplicates and keeping the first spelling.</summary>
    private static void ApplyTags(Host host, IEnumerable<HostTagRequest> tags)
    {
        var requested = tags
            .Select(t => (Name: t.Name.Trim(), Value: t.Value?.Trim() ?? string.Empty))
            .Where(t => t.Name.Length > 0)
            .DistinctBy(t => (t.Name.ToUpperInvariant(), t.Value.ToUpperInvariant()))
            .ToList();

        // Keep rows that are unchanged so their IDs stay stable; remove and add the rest.
        host.Tags.RemoveAll(existing => !requested.Contains((existing.Name, existing.Value)));
        foreach (var (name, value) in requested)
        {
            if (!host.Tags.Any(t => t.Name == name && t.Value == value))
            {
                host.Tags.Add(new HostTag { Name = name, Value = value });
            }
        }
    }

    private void ApplyCredentials(Host host, HostCredentialRequest[] credentials)
    {
        var kept = new List<HostCredential>(credentials.Length);
        for (var index = 0; index < credentials.Length; index++)
        {
            var request = credentials[index];
            HostCredential credential;
            if (request.Id is { } id)
            {
                credential = host.Credentials.FirstOrDefault(c => c.Id == id)
                    ?? throw new InventoryValidationException(
                        $"credentials[{index}].id", "This credential no longer exists on the host. Reload and try again.");
            }
            else
            {
                credential = new HostCredential();
            }

            credential.Label = InventoryText.TrimToNull(request.Label);
            credential.Username = InventoryText.TrimToNull(request.Username);
            credential.Domain = InventoryText.TrimToNull(request.Domain);
            credential.Notes = InventoryText.TrimToNull(request.Notes);
            credential.Position = index;

            // Secrets are stored exactly as entered (no trimming): whitespace can be significant.
            if (!string.IsNullOrEmpty(request.Password))
            {
                credential.ProtectedPassword = protector.Protect(request.Password);
            }
            else if (request.ClearPassword)
            {
                credential.ProtectedPassword = null;
            }

            if (!string.IsNullOrEmpty(request.PrivateKey))
            {
                credential.ProtectedPrivateKey = protector.Protect(request.PrivateKey);
            }
            else if (request.ClearPrivateKey)
            {
                credential.ProtectedPrivateKey = null;
            }

            kept.Add(credential);
        }

        host.Credentials.RemoveAll(existing => !kept.Contains(existing));
        host.Credentials.AddRange(kept.Where(c => !host.Credentials.Contains(c)));
    }
}
