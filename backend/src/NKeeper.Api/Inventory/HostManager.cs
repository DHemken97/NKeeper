using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory;

/// <summary>
/// Manages hosts together with the tags, services and credentials they own.
/// Credential secrets go in encrypted and never come back out.
/// </summary>
public sealed class HostManager(NKeeperDbContext db, HostAccess access, CredentialProtector protector, TimeProvider timeProvider)
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
        h.Services
            .OrderBy(s => s.Position)
            .Select(s => new HostServiceResponse(
                s.Protocol, s.Port, s.Label, s.Protocol == ServiceProtocol.Rdp ? s.RdpSecurity ?? RdpSecurityMode.Auto : null))
            .ToList(),
        h.Credentials
            .OrderBy(c => c.Position)
            .Select(c => new HostCredentialResponse(c.Id, c.Label, c.Username, c.Domain, c.Notes, c.ProtectedPassword != null, c.ProtectedPrivateKey != null))
            .ToList(),
        h.TrustedKeys
            .OrderBy(k => k.Protocol).ThenBy(k => k.Port).ThenBy(k => k.Algorithm)
            .Select(k => new TrustedKeyResponse(k.Protocol, k.Port, k.Algorithm, k.Fingerprint, k.TrustedAtUtc))
            .ToList(),
        h.CreatedAtUtc,
        h.UpdatedAtUtc,
        h.Version,
        false);

    /// <summary>
    /// Every host the user can see, ordered by name. The client searches, groups and sorts it
    /// locally, which keeps the connection tree instant (hundreds to a few thousand hosts).
    /// </summary>
    public async Task<List<HostResponse>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = await access.Visible(user).AsNoTracking()
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
            .Select(Projection)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var manageable = await access.ManageableAsync(user, cancellationToken);
        return [.. hosts.Select(h => h with { CanManage = manageable?.Contains(h.Id) ?? true })];
    }

    /// <returns>The host, or null when it doesn't exist or the user can't see it.</returns>
    public async Task<HostResponse?> GetAsync(Guid id, RequestUser user, CancellationToken cancellationToken)
    {
        var host = await access.Visible(user).AsNoTracking().Where(h => h.Id == id).Select(Projection).AsSplitQuery().FirstOrDefaultAsync(cancellationToken);
        return host is null
            ? null
            : host with { CanManage = await access.GetPermissionAsync(id, user, cancellationToken) == HostPermission.Manage };
    }

    /// <summary>Creates a host. Only administrators may (enforced by the controller).</summary>
    public async Task<HostResponse> CreateAsync(HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        var host = new Host { CreatedAtUtc = timeProvider.GetUtcNow(), CreatedByUserId = userId };
        Apply(host, request, userId, await LoadCopySourcesAsync(request, user, cancellationToken));
        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(host.Id, user, cancellationToken))!;
    }

    /// <returns>The updated host, or null when it does not exist or isn't visible to the user.</returns>
    /// <exception cref="DbUpdateConcurrencyException">The host changed since <see cref="HostRequest.Version"/>.</exception>
    /// <exception cref="HostAccessDeniedException">The user can see the host but not manage it.</exception>
    public async Task<HostResponse?> UpdateAsync(Guid id, HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        if (!await RequireManageAsync(id, user, cancellationToken))
        {
            return null;
        }

        var userId = user.Id;
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
        Apply(host, request, userId, await LoadCopySourcesAsync(request, user, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        // A new address may be a different machine: its keys and certificates must be confirmed again.
        if ((host.Hostname, host.IpAddress) != previousAddress)
        {
            await db.HostTrustedKeys.Where(k => k.HostId == id).ExecuteDeleteAsync(cancellationToken);
        }

        // Editing tags can move a host out of the user's own grants; then it is simply no longer visible.
        return await GetAsync(id, user, cancellationToken);
    }

    /// <summary>Forgets every trusted SSH host key and RDP certificate, so the next connection asks again.</summary>
    /// <returns>False when the host does not exist or isn't visible to the user.</returns>
    public async Task<bool> ForgetTrustedKeysAsync(Guid id, RequestUser user, CancellationToken cancellationToken)
    {
        if (!await RequireManageAsync(id, user, cancellationToken))
        {
            return false;
        }

        await db.HostTrustedKeys.Where(k => k.HostId == id).ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes the host; its tags, services, credentials and access grants go with it (cascade).</summary>
    /// <returns>False when the host does not exist or isn't visible to the user.</returns>
    public async Task<bool> DeleteAsync(Guid id, RequestUser user, CancellationToken cancellationToken) =>
        await RequireManageAsync(id, user, cancellationToken)
        && await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <returns>False when the host is not visible; throws when it is visible but not manageable.</returns>
    private async Task<bool> RequireManageAsync(Guid id, RequestUser user, CancellationToken cancellationToken) =>
        await access.GetPermissionAsync(id, user, cancellationToken) switch
        {
            HostPermission.None => false,
            HostPermission.Manage => true,
            _ => throw new HostAccessDeniedException(),
        };

    /// <summary>The credentials named by CopyFromCredentialId, from hosts the user can manage.</summary>
    private async Task<Dictionary<Guid, HostCredential>> LoadCopySourcesAsync(HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var ids = request.Credentials.Select(c => c.CopyFromCredentialId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await access.Manageable(user).AsNoTracking()
            .SelectMany(h => h.Credentials)
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
    }

    private void Apply(Host host, HostRequest request, Guid userId, Dictionary<Guid, HostCredential> copySources)
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
        ApplyCredentials(host, request.Credentials, copySources);
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
            RdpSecurity = s.Protocol == ServiceProtocol.Rdp && s.RdpSecurity is not RdpSecurityMode.Auto ? s.RdpSecurity : null,
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

    private void ApplyCredentials(Host host, HostCredentialRequest[] credentials, Dictionary<Guid, HostCredential> copySources)
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
            else if (request.CopyFromCredentialId is { } sourceId)
            {
                // Hidden and missing credentials look the same, so nothing leaks about other hosts.
                var source = copySources.GetValueOrDefault(sourceId)
                    ?? throw new InventoryValidationException(
                        $"credentials[{index}].copyFromCredentialId", "The credential to copy no longer exists. Reload and try again.");
                credential = new HostCredential { ProtectedPassword = source.ProtectedPassword, ProtectedPrivateKey = source.ProtectedPrivateKey };
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
