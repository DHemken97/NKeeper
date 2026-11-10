using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory;

/// <summary>
/// Combines duplicate hosts into one: the kept host gets what the others have that it lacks, everything that
/// pointed at the others points at it, and the others are deleted. In one transaction; audited.
/// <list type="bullet">
/// <item>Details (hostname, address, MAC, hardware, Zabbix link, where a VM runs, jump host) are the kept host's, filled in from the others where it has none.</item>
/// <item>Tags, services and credentials are added unless it has them (same tag; same type, port, instance and address; same account, or label).
/// A credential it has takes a password or key it lacks from its duplicate, and the services of both.</item>
/// <item>Trusted keys and certificates come along only from a host at the same address.</item>
/// <item>VMs that run on, and hosts reached through, a removed host now use the kept one; so do access grants given for it.</item>
/// </list>
/// </summary>
public sealed class HostMerger(NKeeperDbContext db, HostAccess access, HostManager hosts, AuditLog audit, TimeProvider timeProvider)
{
    public const int MaxSources = 50;

    /// <returns>The kept host, combined; null when it isn't visible.</returns>
    /// <exception cref="HostAccessDeniedException">The user can't manage one of the hosts.</exception>
    public async Task<HostResponse?> MergeAsync(Guid targetId, IReadOnlyList<Guid> sourceIds, RequestUser user, CancellationToken cancellationToken)
    {
        var sources = sourceIds.Distinct().ToList();
        if (sources.Count == 0 || sources.Contains(targetId))
        {
            throw new InventoryValidationException("sourceIds", "Choose the other hosts to combine into this one.");
        }

        var ids = sources.Append(targetId).ToList();
        if (await access.Visible(user).CountAsync(h => ids.Contains(h.Id), cancellationToken) != ids.Count)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var loaded = await access.Manageable(user).Where(h => ids.Contains(h.Id))
            .Include(h => h.Services).Include(h => h.Tags).Include(h => h.Credentials).Include(h => h.TrustedKeys)
            .AsSplitQuery().ToListAsync(cancellationToken);
        if (loaded.Count != ids.Count)
        {
            throw new HostAccessDeniedException();
        }

        var target = loaded.Single(h => h.Id == targetId);
        var removed = sources.Select(id => loaded.Single(h => h.Id == id)).ToList();
        var serviceMap = new Dictionary<Guid, Guid>();
        var credentialMap = new Dictionary<Guid, Guid>();

        foreach (var source in removed)
        {
            FillDetails(target, source, sources);
            MergeTags(target, source);
            MergeServices(target, source, serviceMap);
            MergeCredentials(target, source, serviceMap, credentialMap);
            MergeTrustedKeys(target, source);
        }

        // A VM can't run on itself, nor be reached through itself.
        if (target.HypervisorId == target.Id)
        {
            (target.HypervisorId, target.HypervisorSeenAtUtc) = (null, null);
        }

        if (target.JumpHostId == target.Id)
        {
            (target.JumpHostId, target.JumpCredentialId) = (null, null);
        }
        else if (target.JumpCredentialId is { } jumpCredential && credentialMap.TryGetValue(jumpCredential, out var mappedJump))
        {
            target.JumpCredentialId = mappedJump;
        }

        target.UpdatedAtUtc = timeProvider.GetUtcNow();
        target.UpdatedByUserId = user.Id;

        // What pointed at the removed hosts points at the kept one.
        foreach (var vm in await db.Hosts.Where(h => h.HypervisorId != null && sources.Contains(h.HypervisorId.Value) && !ids.Contains(h.Id)).ToListAsync(cancellationToken))
        {
            vm.HypervisorId = target.Id;
        }

        foreach (var reached in await db.Hosts.Where(h => h.JumpHostId != null && sources.Contains(h.JumpHostId.Value) && !ids.Contains(h.Id)).ToListAsync(cancellationToken))
        {
            reached.JumpHostId = target.Id;
            reached.JumpCredentialId = reached.JumpCredentialId is { } c && credentialMap.TryGetValue(c, out var mapped) ? mapped : null;
        }

        var grants = await db.HostAccessGrants.Where(g => g.HostId != null && ids.Contains(g.HostId.Value)).ToListAsync(cancellationToken);
        foreach (var byUser in grants.GroupBy(g => g.UserId))
        {
            // One grant per user for the kept host, at the highest level any of them had.
            var keep = byUser.FirstOrDefault(g => g.HostId == target.Id) ?? byUser.First();
            keep.HostId = target.Id;
            keep.Level = byUser.Max(g => g.Level);
            db.HostAccessGrants.RemoveRange(byUser.Where(g => g != keep));
        }

        db.Hosts.RemoveRange(removed);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry("host.merge", target.Id, $"{string.Join(", ", removed.Select(h => h.Name))} → {target.Name}"));
        return await hosts.GetAsync(target.Id, user, cancellationToken);
    }

    private static void FillDetails(Host target, Host source, List<Guid> sources)
    {
        target.Hostname ??= source.Hostname;
        target.IpAddress ??= source.IpAddress;
        target.MacAddress ??= source.MacAddress;
        target.Description ??= source.Description;
        target.CpuCores ??= source.CpuCores;
        target.MemoryMiB ??= source.MemoryMiB;
        target.SerialNumber ??= source.SerialNumber;
        target.ZabbixHostId ??= source.ZabbixHostId;
        if (target.HypervisorId is null && source.HypervisorId is { } hypervisor && !sources.Contains(hypervisor))
        {
            target.IsVirtualMachine = true;
            target.HypervisorId = hypervisor;
            target.HypervisorSeenAtUtc = source.HypervisorSeenAtUtc;
            target.VirtualMachineId ??= source.VirtualMachineId;
        }
        else if (source.IsVirtualMachine && !target.IsVirtualMachine && target.HypervisorId is null)
        {
            target.IsVirtualMachine = true;
        }

        if (target.IsVirtualMachine)
        {
            target.VirtualMachineId ??= source.VirtualMachineId;
        }

        if (target.JumpHostId is null && source.JumpHostId is { } jump && !sources.Contains(jump))
        {
            target.JumpHostId = jump;
            target.JumpCredentialId = source.JumpCredentialId;
        }
    }

    private static void MergeTags(Host target, Host source)
    {
        foreach (var tag in source.Tags)
        {
            if (!target.Tags.Any(t => t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase) && t.Value.Equals(tag.Value, StringComparison.OrdinalIgnoreCase)))
            {
                target.Tags.Add(new HostTag { Name = tag.Name, Value = tag.Value, Inheritable = tag.Inheritable });
            }
        }
    }

    private static string ServiceKey(HostService s) =>
        $"{s.Protocol}|{s.Port}|{s.Instance?.ToUpperInvariant()}|{s.Address?.ToUpperInvariant()}";

    private void MergeServices(Host target, Host source, Dictionary<Guid, Guid> serviceMap)
    {
        foreach (var service in source.Services.OrderBy(s => s.Position))
        {
            if (target.Services.FirstOrDefault(s => ServiceKey(s) == ServiceKey(service)) is { } same)
            {
                serviceMap[service.Id] = same.Id;
                continue;
            }

            var copy = new HostService
            {
                Id = Guid.NewGuid(),
                Protocol = service.Protocol,
                Port = service.Port,
                Label = service.Label,
                Position = target.Services.Count == 0 ? 0 : target.Services.Max(s => s.Position) + 1,
                RdpSecurity = service.RdpSecurity,
                Address = service.Address,
                Instance = service.Instance,
                Tools = service.Tools is null ? null : [.. service.Tools],
            };
            target.Services.Add(copy);
            db.Entry(copy).State = EntityState.Added;
            serviceMap[service.Id] = copy.Id;
        }
    }

    private static bool SameAccount(HostCredential a, HostCredential b) =>
        string.IsNullOrWhiteSpace(a.Username) && string.IsNullOrWhiteSpace(b.Username)
            ? string.Equals(a.Label?.Trim(), b.Label?.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Username?.Trim(), b.Username?.Trim(), StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.Domain?.Trim() ?? string.Empty, b.Domain?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void MergeCredentials(Host target, Host source, Dictionary<Guid, Guid> serviceMap, Dictionary<Guid, Guid> credentialMap)
    {
        List<Guid>? Mapped(List<Guid>? serviceIds) =>
            serviceIds is null ? null : [.. serviceIds.Where(serviceMap.ContainsKey).Select(id => serviceMap[id]).Distinct()];

        foreach (var credential in source.Credentials.OrderBy(c => c.Position))
        {
            if (target.Credentials.FirstOrDefault(c => SameAccount(c, credential)) is { } same)
            {
                same.ProtectedPassword ??= credential.ProtectedPassword;
                same.ProtectedPrivateKey ??= credential.ProtectedPrivateKey;
                same.Notes ??= credential.Notes;
                same.Label ??= credential.Label;
                // Used with what either was used with; with all services when either was.
                same.ServiceIds = same.ServiceIds is null || credential.ServiceIds is null ? null : [.. same.ServiceIds.Union(Mapped(credential.ServiceIds)!)];
                credentialMap[credential.Id] = same.Id;
                continue;
            }

            var copy = new HostCredential
            {
                Id = Guid.NewGuid(),
                Label = credential.Label,
                Username = credential.Username,
                Domain = credential.Domain,
                ProtectedPassword = credential.ProtectedPassword,
                ProtectedPrivateKey = credential.ProtectedPrivateKey,
                Notes = credential.Notes,
                Position = target.Credentials.Count == 0 ? 0 : target.Credentials.Max(c => c.Position) + 1,
                ServiceIds = Mapped(credential.ServiceIds),
            };
            target.Credentials.Add(copy);
            db.Entry(copy).State = EntityState.Added;
            credentialMap[credential.Id] = copy.Id;
        }
    }

    /// <summary>Keys and certificates are trusted for an address: they only come from a duplicate at the same one.</summary>
    private static void MergeTrustedKeys(Host target, Host source)
    {
        if ((target.Hostname, target.IpAddress) != (source.Hostname, source.IpAddress))
        {
            return;
        }

        foreach (var key in source.TrustedKeys)
        {
            if (!target.TrustedKeys.Any(k => k.Protocol == key.Protocol && k.Port == key.Port && k.Algorithm == key.Algorithm))
            {
                target.TrustedKeys.Add(new HostTrustedKey
                {
                    Protocol = key.Protocol,
                    Port = key.Port,
                    Algorithm = key.Algorithm,
                    Fingerprint = key.Fingerprint,
                    TrustedAtUtc = key.TrustedAtUtc,
                    TrustedByUserId = key.TrustedByUserId,
                });
            }
        }
    }
}
