using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Plugins;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Plugins;
using NKeeper.Sdk.Access;
using NKeeper.Sdk.Inventory;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory;

/// <summary>
/// Manages hosts together with the tags, services, credentials and plugin values they own.
/// Credential secrets go in encrypted and never come back out. Plugins reach it as
/// <see cref="IHostDirectory"/>.
/// </summary>
public sealed class HostManager(NKeeperDbContext db, HostAccess access, CredentialProtector protector, HostFieldRegistry hostFields, TimeProvider timeProvider)
    : IHostDirectory
{
    private static readonly Expression<Func<Host, HostResponse>> Projection = h => new HostResponse(
        h.Id,
        h.Name,
        h.Hostname,
        h.IpAddress,
        h.MacAddress,
        h.Description,
        h.IsVirtualMachine,
        h.HypervisorId,
        h.VirtualMachineId,
        h.HypervisorSeenAtUtc,
        h.CpuCores,
        h.MemoryMiB,
        h.SerialNumber,
        h.JumpHostId,
        h.JumpCredentialId,
        PluginValueMap(h.PluginValues.Select(v => new PluginValueRow(v.PluginId, v.Name, v.Value)).ToList()),
        h.Tags
            .OrderBy(t => t.Name.ToUpper()).ThenBy(t => t.Value.ToUpper())
            .Select(t => new HostTagResponse(t.Name, t.Value == "" ? null : t.Value))
            .ToList(),
        h.Services
            .OrderBy(s => s.Position)
            .Select(s => new HostServiceResponse(
                s.Protocol, s.Port, s.Label, s.Protocol == ServiceProtocol.Rdp ? s.RdpSecurity ?? RdpSecurityMode.Auto : null, s.Address))
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
        var host = new Host { Id = Guid.NewGuid(), CreatedAtUtc = timeProvider.GetUtcNow(), CreatedByUserId = userId };
        await CheckHypervisorAsync(host.Id, request.IsVirtualMachine ? request.HypervisorId : null, user, cancellationToken);
        await CheckJumpHostAsync(host.Id, request, user, cancellationToken);
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
            .Include(h => h.PluginValues)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        db.Entry(host).Property(h => h.Version).OriginalValue = version;
        await CheckHypervisorAsync(id, request.IsVirtualMachine ? request.HypervisorId : null, user, cancellationToken);
        await CheckJumpHostAsync(id, request, user, cancellationToken);
        var previousAddress = (host.Hostname, host.IpAddress);
        var previousIdracAddresses = IdracAddresses(host);
        Apply(host, request, userId, await LoadCopySourcesAsync(request, user, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        // A new address may be a different machine: its keys and certificates must be confirmed again.
        if ((host.Hostname, host.IpAddress) != previousAddress)
        {
            await db.HostTrustedKeys.Where(k => k.HostId == id).ExecuteDeleteAsync(cancellationToken);
        }
        else if (!IdracAddresses(host).SetEquals(previousIdracAddresses))
        {
            // The iDRAC has its own address, and identities trusted for it are kept under its protocol.
            await db.HostTrustedKeys.Where(k => k.HostId == id && k.Protocol == ServiceProtocol.Idrac).ExecuteDeleteAsync(cancellationToken);
        }

        // Editing tags can move a host out of the user's own grants; then it is simply no longer visible.
        return await GetAsync(id, user, cancellationToken);
    }

    /// <summary>
    /// Records where virtual machines run, as found by scanning their hypervisors: each guest becomes
    /// a VM on that host, with its platform identity and the time it was seen there.
    /// </summary>
    /// <returns>The updated hosts.</returns>
    /// <exception cref="HostAccessDeniedException">A guest is missing or the user can't manage it.</exception>
    public async Task<List<HostResponse>> LinkVirtualMachinesAsync(IReadOnlyList<HypervisorLinkRequest> links, RequestUser user, CancellationToken cancellationToken)
    {
        var ids = links.Select(l => l.GuestId).Distinct().ToList();
        var guests = await access.Manageable(user).Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, cancellationToken);
        if (guests.Count != ids.Count)
        {
            throw new HostAccessDeniedException();
        }

        var now = timeProvider.GetUtcNow();
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            await CheckHypervisorAsync(link.GuestId, link.HypervisorId, user, cancellationToken, $"links[{index}].hypervisorId");
            var guest = guests[link.GuestId];
            guest.IsVirtualMachine = true;
            guest.HypervisorId = link.HypervisorId;
            guest.VirtualMachineId = InventoryText.TrimToNull(link.VirtualMachineId) ?? guest.VirtualMachineId;
            guest.CpuCores = link.CpuCores ?? guest.CpuCores;
            guest.MemoryMiB = link.MemoryMiB ?? guest.MemoryMiB;
            guest.HypervisorSeenAtUtc = now;
            guest.UpdatedAtUtc = now;
            guest.UpdatedByUserId = user.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        var updated = new List<HostResponse>(ids.Count);
        foreach (var id in ids)
        {
            if (await GetAsync(id, user, cancellationToken) is { } host)
            {
                updated.Add(host);
            }
        }

        return updated;
    }

    /// <summary>
    /// Adds hosts for VMs and containers found by scanning their hypervisor, each already linked to
    /// it (with its VM identity and when it was seen). Administrators only (enforced by the controller).
    /// </summary>
    /// <returns>The new hosts.</returns>
    public async Task<List<HostResponse>> CreateFromScanAsync(IReadOnlyList<ScannedHostRequest> requests, RequestUser user, CancellationToken cancellationToken)
    {
        var hypervisors = requests.Select(r => r.HypervisorId).Distinct().ToList();
        var visible = await access.Visible(user).Where(h => hypervisors.Contains(h.Id)).Select(h => h.Id).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hosts = new List<Host>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var field = $"hosts[{index}]";
            var hostname = InventoryText.TrimToNull(request.Hostname);
            string? ip = null, mac = null;
            if (hostname is not null && !NetworkAddress.IsValidHostname(hostname))
            {
                throw new InventoryValidationException($"{field}.hostname", $"“{hostname}” isn't a valid hostname.");
            }

            if (InventoryText.TrimToNull(request.IpAddress) is { } rawIp && !NetworkAddress.TryNormalizeIp(rawIp, out ip))
            {
                throw new InventoryValidationException($"{field}.ipAddress", $"“{rawIp}” isn't a valid IP address.");
            }

            if (hostname is null && ip is null)
            {
                throw new InventoryValidationException($"{field}.hostname", $"“{request.Name}” needs a hostname or an IP address.");
            }

            if (InventoryText.TrimToNull(request.MacAddress) is { } rawMac && !NetworkAddress.TryNormalizeMac(rawMac, out mac))
            {
                mac = null; // A MAC address is a nice-to-have: leave it out rather than refuse the host.
            }

            if (!visible.Contains(request.HypervisorId))
            {
                throw new InventoryValidationException($"{field}.hypervisorId", "The host it runs on no longer exists.");
            }

            hosts.Add(new Host
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Hostname = hostname,
                IpAddress = ip,
                MacAddress = mac,
                Description = InventoryText.TrimToNull(request.Description),
                IsVirtualMachine = true,
                HypervisorId = request.HypervisorId,
                VirtualMachineId = InventoryText.TrimToNull(request.VirtualMachineId),
                CpuCores = request.CpuCores,
                MemoryMiB = request.MemoryMiB,
                HypervisorSeenAtUtc = now,
                CreatedAtUtc = now,
                CreatedByUserId = user.Id,
                UpdatedAtUtc = now,
                UpdatedByUserId = user.Id,
            });
        }

        db.Hosts.AddRange(hosts);
        await db.SaveChangesAsync(cancellationToken);
        var ids = hosts.Select(h => h.Id).ToList();
        var created = await access.Visible(user).AsNoTracking().Where(h => ids.Contains(h.Id))
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
            .Select(Projection).AsSplitQuery().ToListAsync(cancellationToken);
        return [.. created.Select(h => h with { CanManage = true })];
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// Applies the same tag, hypervisor and service changes to many hosts in one transaction.
    /// Hosts the changes don't affect are left untouched (their update time doesn't move).
    /// </summary>
    /// <returns>Every selected host, as it is now.</returns>
    /// <exception cref="HostAccessDeniedException">A host is missing or the user can't manage it.</exception>
    public async Task<List<HostResponse>> BulkUpdateAsync(BulkHostUpdateRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var ids = request.HostIds.Distinct().ToHashSet();
        var hosts = await access.Manageable(user).Where(h => ids.Contains(h.Id))
            .Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery().ToListAsync(cancellationToken);
        if (hosts.Count != ids.Count)
        {
            throw new HostAccessDeniedException();
        }

        if (request.Hypervisor is { HypervisorId: { } hypervisorId })
        {
            await CheckBulkHypervisorAsync(hypervisorId, ids, user, cancellationToken);
        }

        static string Norm(string? value) => value?.Trim() ?? string.Empty;
        var removals = request.RemoveTags.Select(r => (Name: Norm(r.Name), Value: r.Value is null ? null : Norm(r.Value))).ToList();
        var sets = request.SetTags.Select(t => (Name: Norm(t.Name), Value: Norm(t.Value))).ToList();
        var additions = request.AddTags.Select(t => (Name: Norm(t.Name), Value: Norm(t.Value))).ToList();
        var now = timeProvider.GetUtcNow();
        var idracChanged = new List<Guid>();
        var lostSsh = new List<Guid>();

        foreach (var host in hosts)
        {
            var changed = false;
            var previousIdracAddresses = IdracAddresses(host);
            bool Is(HostTag tag, string name, string? value) =>
                tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (value is null || tag.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

            foreach (var (name, value) in removals)
            {
                changed |= host.Tags.RemoveAll(t => Is(t, name, value)) > 0;
            }

            foreach (var (name, value) in sets)
            {
                if (host.Tags.Count(t => Is(t, name, null)) == 1 && host.Tags.Any(t => Is(t, name, value)))
                {
                    continue; // Already exactly this.
                }

                host.Tags.RemoveAll(t => Is(t, name, null));
                host.Tags.Add(new HostTag { Name = name, Value = value });
                changed = true;
            }

            foreach (var (name, value) in additions)
            {
                if (!host.Tags.Any(t => Is(t, name, value)))
                {
                    host.Tags.Add(new HostTag { Name = name, Value = value });
                    changed = true;
                }
            }

            if (host.Tags.Count > 50)
            {
                throw new InventoryValidationException("addTags", $"“{host.Name}” would have more than 50 tags.");
            }

            if (request.Hypervisor is { } hypervisor)
            {
                var target = hypervisor.HypervisorId;
                if (host.HypervisorId != target || (target is not null && !host.IsVirtualMachine))
                {
                    host.IsVirtualMachine |= target is not null;
                    host.HypervisorId = target;
                    host.HypervisorSeenAtUtc = null;
                    changed = true;
                }
            }

            var hadSsh = host.Services.Any(s => s.Protocol == ServiceProtocol.Ssh);
            foreach (var removal in request.RemoveServices)
            {
                changed |= host.Services.RemoveAll(s => s.Protocol == removal.Protocol && (removal.Port is null || s.Port == removal.Port)) > 0;
            }

            foreach (var service in request.AddServices)
            {
                if (host.Services.Any(s => s.Protocol == service.Protocol && s.Port == service.Port))
                {
                    continue; // Already has it; its label and settings are kept.
                }

                host.Services.Add(new HostService
                {
                    Protocol = service.Protocol!.Value,
                    Port = service.Port,
                    Label = InventoryText.TrimToNull(service.Label),
                    Position = host.Services.Count == 0 ? 0 : host.Services.Max(s => s.Position) + 1,
                    RdpSecurity = service.Protocol == ServiceProtocol.Rdp && service.RdpSecurity is not RdpSecurityMode.Auto ? service.RdpSecurity : null,
                });
                changed = true;
            }

            if (host.Services.Count > 20)
            {
                throw new InventoryValidationException("addServices", $"“{host.Name}” would have more than 20 services.");
            }

            changed |= ApplyBulkCredentials(host, request);

            if (hadSsh && !host.Services.Any(s => s.Protocol == ServiceProtocol.Ssh))
            {
                lostSsh.Add(host.Id);
            }

            if (!IdracAddresses(host).SetEquals(previousIdracAddresses))
            {
                idracChanged.Add(host.Id);
            }

            if (changed)
            {
                host.UpdatedAtUtc = now;
                host.UpdatedByUserId = user.Id;
            }
        }

        // Hosts others are reached through must keep an SSH service.
        if (lostSsh.Count > 0)
        {
            var jumpHostId = await db.Hosts.AsNoTracking().Where(h => h.JumpHostId != null && lostSsh.Contains(h.JumpHostId.Value))
                .Select(h => h.JumpHostId).FirstOrDefaultAsync(cancellationToken);
            if (jumpHostId is { } id)
            {
                var name = hosts.First(h => h.Id == id).Name;
                throw new InventoryValidationException("removeServices", $"Other hosts are reached through “{name}”, so it needs its SSH service.");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (idracChanged.Count > 0)
        {
            // Certificates trusted for an iDRAC belong to its address.
            await db.HostTrustedKeys.Where(k => idracChanged.Contains(k.HostId) && k.Protocol == ServiceProtocol.Idrac).ExecuteDeleteAsync(cancellationToken);
        }

        var updated = await access.Visible(user).AsNoTracking().Where(h => ids.Contains(h.Id))
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
            .Select(Projection).AsSplitQuery().ToListAsync(cancellationToken);
        return [.. updated.Select(h => h with { CanManage = true })];
    }

    /// <summary>
    /// Removes, then updates the secrets of, the matching credentials, then adds new ones after the
    /// host's own (skipping accounts, or labels, it already has).
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    private bool ApplyBulkCredentials(Host host, BulkHostUpdateRequest request)
    {
        static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        static bool Matches(HostCredential credential, BulkCredentialMatch match) =>
            (string.IsNullOrWhiteSpace(match.Username) || Same(credential.Username, match.Username))
            && (string.IsNullOrWhiteSpace(match.Domain) || Same(credential.Domain, match.Domain))
            && (string.IsNullOrWhiteSpace(match.Label) || Same(credential.Label, match.Label));

        var changed = false;
        foreach (var removal in request.RemoveCredentials)
        {
            // Hosts reached through this one fall back to its first usable credential (the reference is cleared).
            changed |= host.Credentials.RemoveAll(c => Matches(c, removal)) > 0;
        }

        foreach (var update in request.UpdateCredentials)
        {
            foreach (var credential in host.Credentials.Where(c => Matches(c, update)))
            {
                // Secrets are stored exactly as entered (no trimming): whitespace can be significant.
                if (!string.IsNullOrEmpty(update.Password))
                {
                    credential.ProtectedPassword = protector.Protect(update.Password);
                }
                else if (update.ClearPassword)
                {
                    credential.ProtectedPassword = null;
                }

                if (!string.IsNullOrEmpty(update.PrivateKey))
                {
                    credential.ProtectedPrivateKey = protector.Protect(update.PrivateKey);
                }
                else if (update.ClearPrivateKey)
                {
                    credential.ProtectedPrivateKey = null;
                }

                changed = true;
            }
        }

        foreach (var addition in request.AddCredentials)
        {
            var exists = string.IsNullOrWhiteSpace(addition.Username)
                ? host.Credentials.Any(c => string.IsNullOrWhiteSpace(c.Username) && Same(c.Label, addition.Label))
                : host.Credentials.Any(c => Same(c.Username, addition.Username) && Same(c.Domain, addition.Domain));
            if (exists)
            {
                continue;
            }

            host.Credentials.Add(new HostCredential
            {
                Label = InventoryText.TrimToNull(addition.Label),
                Username = InventoryText.TrimToNull(addition.Username),
                Domain = InventoryText.TrimToNull(addition.Domain),
                Notes = InventoryText.TrimToNull(addition.Notes),
                Position = host.Credentials.Count == 0 ? 0 : host.Credentials.Max(c => c.Position) + 1,
                ProtectedPassword = string.IsNullOrEmpty(addition.Password) ? null : protector.Protect(addition.Password),
                ProtectedPrivateKey = string.IsNullOrEmpty(addition.PrivateKey) ? null : protector.Protect(addition.PrivateKey),
            });
            changed = true;
        }

        if (host.Credentials.Count > 20)
        {
            throw new InventoryValidationException("addCredentials", $"“{host.Name}” would have more than 20 credentials.");
        }

        return changed;
    }

    /// <summary>The hypervisor must be visible, not one of the hosts, and not run (eventually) on one of them.</summary>
    private async Task CheckBulkHypervisorAsync(Guid hypervisorId, HashSet<Guid> hostIds, RequestUser user, CancellationToken cancellationToken)
    {
        if (hostIds.Contains(hypervisorId))
        {
            throw new InventoryValidationException("hypervisor", "The host they run on can't be one of the selected hosts.");
        }

        if (!await access.Visible(user).AnyAsync(h => h.Id == hypervisorId, cancellationToken))
        {
            throw new InventoryValidationException("hypervisor", "That host no longer exists. Choose another.");
        }

        var links = await db.Hosts.AsNoTracking()
            .Where(h => h.HypervisorId != null)
            .Select(h => new { h.Id, h.HypervisorId })
            .ToDictionaryAsync(h => h.Id, h => h.HypervisorId!.Value, cancellationToken);
        var seen = new HashSet<Guid>();
        for (Guid? current = hypervisorId; current is { } next && seen.Add(next); current = links.TryGetValue(next, out var parent) ? parent : null)
        {
            if (hostIds.Contains(next))
            {
                throw new InventoryValidationException("hypervisor", "That host runs on one of the selected hosts, so they can't run on it.");
            }
        }
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

    /// <summary>
    /// A VM's hypervisor must be another host the user can see, and following "runs on" from it
    /// must not lead back to this host (nested virtualisation is fine; a loop isn't).
    /// </summary>
    private async Task CheckHypervisorAsync(Guid hostId, Guid? requested, RequestUser user, CancellationToken cancellationToken, string field = "hypervisorId")
    {
        if (requested is not { } hypervisorId)
        {
            return;
        }

        if (hypervisorId == hostId)
        {
            throw new InventoryValidationException(field, "A virtual machine can't run on itself.");
        }

        if (!await access.Visible(user).AnyAsync(h => h.Id == hypervisorId, cancellationToken))
        {
            throw new InventoryValidationException(field, "The host it runs on no longer exists. Choose another.");
        }

        var links = await db.Hosts.AsNoTracking()
            .Where(h => h.HypervisorId != null)
            .Select(h => new { h.Id, h.HypervisorId })
            .ToDictionaryAsync(h => h.Id, h => h.HypervisorId!.Value, cancellationToken);
        var seen = new HashSet<Guid>();
        for (Guid? current = hypervisorId; current is { } next && seen.Add(next); current = links.TryGetValue(next, out var parent) ? parent : null)
        {
            if (next == hostId)
            {
                throw new InventoryValidationException(field, "That host runs on this one, so this one can't run on it.");
            }
        }
    }

    /// <summary>
    /// A jump host must be another host the user can see, with an SSH service, and not reached
    /// through a jump host itself (one hop); hosts that are jump hosts can't get one. Its credential,
    /// when given, must be one of its own.
    /// </summary>
    private async Task CheckJumpHostAsync(Guid hostId, HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        if (request.JumpHostId is not { } jumpId)
        {
            return;
        }

        if (jumpId == hostId)
        {
            throw new InventoryValidationException("jumpHostId", "A host can't be reached through itself.");
        }

        var jump = await access.Visible(user).AsNoTracking()
            .Where(h => h.Id == jumpId)
            .Select(h => new { h.JumpHostId, HasSsh = h.Services.Any(s => s.Protocol == ServiceProtocol.Ssh), Credentials = h.Credentials.Select(c => c.Id).ToList() })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InventoryValidationException("jumpHostId", "The jump host no longer exists. Choose another.");
        if (!jump.HasSsh)
        {
            throw new InventoryValidationException("jumpHostId", "The jump host needs an SSH service.");
        }

        if (jump.JumpHostId is not null)
        {
            throw new InventoryValidationException("jumpHostId", "That host is itself reached through a jump host. Choose one NKeeper reaches directly.");
        }

        if (await db.Hosts.AnyAsync(h => h.JumpHostId == hostId, cancellationToken))
        {
            throw new InventoryValidationException("jumpHostId", "Other hosts are reached through this one, so it can't use a jump host itself.");
        }

        if (request.JumpCredentialId is { } credentialId && !jump.Credentials.Contains(credentialId))
        {
            throw new InventoryValidationException("jumpCredentialId", "That credential isn't one of the jump host's. Choose another.");
        }
    }

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
        host.CpuCores = request.CpuCores;
        host.MemoryMiB = request.MemoryMiB;
        host.SerialNumber = InventoryText.TrimToNull(request.SerialNumber);
        host.JumpHostId = request.JumpHostId;
        host.JumpCredentialId = request.JumpHostId is null ? null : request.JumpCredentialId;
        ApplyVirtualMachine(host, request);
        host.UpdatedAtUtc = timeProvider.GetUtcNow();
        host.UpdatedByUserId = userId;

        ApplyServices(host, request.Services);
        ApplyTags(host, request.Tags);
        ApplyCredentials(host, request.Credentials, copySources);
        foreach (var (pluginId, fields) in request.Plugins ?? [])
        {
            foreach (var (name, value) in fields)
            {
                ApplyPluginValue(host, pluginId, name, value);
            }
        }
    }

    /// <summary>Sets (or removes) one plugin value on a host whose <see cref="Host.PluginValues"/> are loaded.</summary>
    /// <returns>Whether it changed.</returns>
    private bool ApplyPluginValue(Host host, string pluginId, string name, string? value)
    {
        var normalized = hostFields.Normalize(pluginId, name, value);
        var existing = host.PluginValues.FirstOrDefault(v => v.PluginId == pluginId && v.Name == name);
        if (normalized is null)
        {
            return existing is not null && host.PluginValues.Remove(existing);
        }

        if (existing is null)
        {
            host.PluginValues.Add(new HostPluginValue { HostId = host.Id, PluginId = pluginId, Name = name, Value = normalized });
            return true;
        }

        if (existing.Value == normalized)
        {
            return false;
        }

        existing.Value = normalized;
        return true;
    }

    private sealed record PluginValueRow(string PluginId, string Name, string Value);

    private static Dictionary<string, IReadOnlyDictionary<string, string>> PluginValueMap(List<PluginValueRow> rows) =>
        rows.GroupBy(r => r.PluginId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, IReadOnlyDictionary<string, string> (g) => g.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    async Task<IReadOnlyList<HostResponse>> IHostDirectory.GetAsync(IReadOnlyCollection<Guid> ids, RequestUser user, CancellationToken cancellationToken) =>
        await GetManyAsync(ids, user, cancellationToken);

    async Task<IReadOnlyList<HostResponse>> IHostDirectory.SetPluginValuesAsync(
        string pluginId, string field, IReadOnlyDictionary<Guid, string?> values, RequestUser user, CancellationToken cancellationToken)
    {
        var ids = values.Keys.ToList();
        var hosts = await access.Manageable(user).Where(h => ids.Contains(h.Id)).Include(h => h.PluginValues).ToListAsync(cancellationToken);
        if (hosts.Count != ids.Count)
        {
            throw new HostAccessDeniedException();
        }

        var now = timeProvider.GetUtcNow();
        foreach (var host in hosts)
        {
            if (ApplyPluginValue(host, pluginId, field, values[host.Id]))
            {
                host.UpdatedAtUtc = now;
                host.UpdatedByUserId = user.Id;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetManyAsync(ids, user, cancellationToken);
    }

    async Task<IReadOnlyList<HostResponse>> IHostDirectory.CreateAsync(IReadOnlyList<NewHost> newHosts, RequestUser user, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hosts = new List<Host>();
        foreach (var item in newHosts)
        {
            var hostname = item.Hostname is { } name && NetworkAddress.IsValidHostname(name.Trim()) ? name.Trim() : null;
            var ip = NetworkAddress.TryNormalizeIp(item.IpAddress ?? string.Empty, out var normalizedIp) ? normalizedIp : null;
            if (hostname is null && ip is null)
            {
                throw new InventoryValidationException("hostname", $"{item.Name} has neither a valid hostname nor an IP address.");
            }

            var host = new Host
            {
                Id = Guid.NewGuid(),
                Name = Truncate(InventoryText.TrimToNull(item.Name) ?? hostname ?? ip!, 200),
                Hostname = hostname,
                IpAddress = ip,
                MacAddress = NetworkAddress.TryNormalizeMac(item.MacAddress ?? string.Empty, out var mac) ? mac : null,
                Description = InventoryText.TrimToNull(item.Description) is { } description ? Truncate(description, 4000) : null,
                SerialNumber = InventoryText.TrimToNull(item.SerialNumber) is { } serial ? Truncate(serial, 128) : null,
                CreatedAtUtc = now,
                CreatedByUserId = user.Id,
                UpdatedAtUtc = now,
                UpdatedByUserId = user.Id,
            };
            ApplyTags(host, item.Tags
                // "name:value" is NKeeper's search syntax, so tag names can't have a colon.
                .Where(t => t.Name.Trim().Length is > 0 and <= HostTag.NameMaxLength && !t.Name.Contains(':', StringComparison.Ordinal))
                .Where(t => (t.Value?.Length ?? 0) <= HostTag.ValueMaxLength)
                .Take(50)
                .Select(t => new HostTagRequest { Name = t.Name.Trim(), Value = t.Value }));
            foreach (var (pluginId, fields) in item.Plugins)
            {
                foreach (var (field, value) in fields)
                {
                    ApplyPluginValue(host, pluginId, field, value);
                }
            }

            hosts.Add(host);
        }

        db.Hosts.AddRange(hosts);
        await db.SaveChangesAsync(cancellationToken);
        return await GetManyAsync([.. hosts.Select(h => h.Id)], user, cancellationToken);
    }

    private async Task<List<HostResponse>> GetManyAsync(IReadOnlyCollection<Guid> ids, RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = await access.Visible(user).AsNoTracking().Where(h => ids.Contains(h.Id))
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
            .Select(Projection).AsSplitQuery().ToListAsync(cancellationToken);
        var manageable = await access.ManageableAsync(user, cancellationToken);
        return [.. hosts.Select(h => h with { CanManage = manageable?.Contains(h.Id) ?? true })];
    }

    private static HashSet<string> IdracAddresses(Host host) =>
        [.. host.Services.Where(s => s.Protocol == ServiceProtocol.Idrac && s.Address is not null).Select(s => s.Address!)];

    /// <summary>Only virtual machines have a hypervisor. A link chosen by hand has no "seen by a scan" time.</summary>
    private static void ApplyVirtualMachine(Host host, HostRequest request)
    {
        host.IsVirtualMachine = request.IsVirtualMachine;
        var hypervisor = request.IsVirtualMachine ? request.HypervisorId : null;
        if (host.HypervisorId != hypervisor)
        {
            host.HypervisorId = hypervisor;
            host.HypervisorSeenAtUtc = null;
        }

        if (!request.IsVirtualMachine)
        {
            host.VirtualMachineId = null;
        }
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
            Address = s.Protocol == ServiceProtocol.Idrac ? NormalizeAddress(s.Address) : null,
        }));
    }

    /// <summary>An IP address in canonical form, or a trimmed hostname.</summary>
    private static string? NormalizeAddress(string? address)
    {
        var trimmed = InventoryText.TrimToNull(address);
        return trimmed is not null && NetworkAddress.TryNormalizeIp(trimmed, out var ip) ? ip : trimmed;
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
