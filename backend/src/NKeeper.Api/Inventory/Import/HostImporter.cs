using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory.Import;

/// <summary>
/// Imports an mRemoteNG connection file into the inventory. Each connection becomes a host (one
/// service, and a credential when it has a login), its folder path becomes a <c>folder</c> tag, and
/// connections with the same name and address merge into one host. Hosts match connections by name.
/// Everything is saved in one transaction, or not at all for a dry run.
/// </summary>
public sealed class HostImporter(NKeeperDbContext db, HostAccess access, CredentialProtector protector, TimeProvider timeProvider)
{
    /// <summary>The tag holding a host's mRemoteNG folder path, e.g. <c>folder: Servers/SQL</c>.</summary>
    public const string FolderTag = "folder";

    private const int MaxServices = 20;
    private const int MaxCredentials = 20;

    /// <summary>Default ports, as the client shows them: an imported port equal to its default is stored as "default".</summary>
    private static readonly Dictionary<ServiceProtocol, int> DefaultPorts = new()
    {
        [ServiceProtocol.Ssh] = 22,
        [ServiceProtocol.Rdp] = 3389,
        [ServiceProtocol.Vnc] = 5900,
        [ServiceProtocol.Telnet] = 23,
        [ServiceProtocol.Http] = 80,
        [ServiceProtocol.Https] = 443,
        [ServiceProtocol.PowerShell] = 5985,
    };

    /// <exception cref="InventoryValidationException">The file can't be read or the master password is wrong.</exception>
    /// <exception cref="DbUpdateConcurrencyException">A host changed while the import was saving.</exception>
    public async Task<HostImportResponse> ImportAsync(HostImportRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var connections = MRemoteNgFile.Read(request.Content, request.MasterPassword);
        var skipped = new List<HostImportSkip>();
        var warnings = new List<string>();
        var imported = Plan(connections, skipped, warnings);

        // Hosts outside the user's reach are neither matched nor deleted.
        var hosts = await access.Manageable(user)
            .Include(h => h.Services)
            .Include(h => h.Tags)
            .Include(h => h.Credentials)
            .Include(h => h.TrustedKeys)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var byName = hosts.ToLookup(h => h.Name, StringComparer.OrdinalIgnoreCase);

        List<string> added = [], updated = [], unchanged = [], deleted = [];
        foreach (var host in imported)
        {
            var matches = byName[host.Name].ToList();
            if (matches.Count > 1)
            {
                skipped.Add(new(host.Name, $"{matches.Count} hosts in NKeeper have this name, so it's unclear which one to update."));
            }
            else if (matches.Count == 1)
            {
                if (!request.Update)
                {
                    skipped.Add(new(host.Name, "Already in NKeeper, and Update is off."));
                }
                else if (UpdateHost(matches[0], host, user.Id, warnings))
                {
                    updated.Add(host.Name);
                }
                else
                {
                    unchanged.Add(host.Name);
                }
            }
            else if (request.Add)
            {
                db.Hosts.Add(CreateHost(host, user.Id, warnings));
                added.Add(host.Name);
            }
            else
            {
                skipped.Add(new(host.Name, "Not in NKeeper, and Add is off."));
            }
        }

        if (request.Delete)
        {
            // Every name in the file counts, including connections that were skipped: their hosts stay.
            var namesInFile = connections.Select(c => c.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (namesInFile.Count == 0)
            {
                throw new InventoryValidationException("delete", "The file has no connections, so Delete would remove every host.");
            }

            foreach (var host in hosts.Where(h => !namesInFile.Contains(h.Name)).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
            {
                db.Hosts.Remove(host);
                deleted.Add(host.Name);
            }
        }

        if (!request.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new HostImportResponse(request.DryRun, added, updated, unchanged, deleted, skipped, warnings);
    }

    /// <summary>Turns connections into hosts: validates addresses and merges same-name connections.</summary>
    private static List<ImportedHost> Plan(List<MRemoteNgConnection> connections, List<HostImportSkip> skipped, List<string> warnings)
    {
        var hosts = new List<ImportedHost>();
        var byName = new Dictionary<string, ImportedHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in connections)
        {
            var name = connection.Name;
            if (name.Length == 0)
            {
                skipped.Add(new("(no name)", $"A connection to “{connection.Hostname}” has no name."));
                continue;
            }

            if (name.Length > 200)
            {
                skipped.Add(new(name, "The name is longer than 200 characters."));
                continue;
            }

            string? hostname = null, ip = null;
            if (connection.Hostname.Length == 0)
            {
                skipped.Add(new(name, "It has no hostname or IP address."));
                continue;
            }

            if (NetworkAddress.TryNormalizeIp(connection.Hostname, out var normalized))
            {
                ip = normalized;
            }
            else if (NetworkAddress.IsValidHostname(connection.Hostname))
            {
                hostname = connection.Hostname;
            }
            else
            {
                skipped.Add(new(name, $"“{connection.Hostname}” isn't a valid hostname or IP address."));
                continue;
            }

            if (connection.PasswordProblem is { } problem)
            {
                warnings.Add($"{name}: {problem}.");
            }

            if (!byName.TryGetValue(name, out var host))
            {
                host = new ImportedHost(name, hostname, ip);
                byName[name] = host;
                hosts.Add(host);
            }
            else if (!string.Equals(host.Hostname, hostname, StringComparison.OrdinalIgnoreCase) || host.IpAddress != ip)
            {
                skipped.Add(new(name, "Another connection in the file has this name and a different address; only the first was imported."));
                continue;
            }

            host.Merge(connection, warnings);
        }

        return hosts;
    }

    private Host CreateHost(ImportedHost imported, Guid userId, List<string> warnings)
    {
        var now = timeProvider.GetUtcNow();
        var host = new Host
        {
            Name = imported.Name,
            Hostname = imported.Hostname,
            IpAddress = imported.IpAddress,
            MacAddress = imported.MacAddress,
            Description = imported.Description,
            CreatedAtUtc = now,
            CreatedByUserId = userId,
            UpdatedAtUtc = now,
            UpdatedByUserId = userId,
        };
        UpdateHost(host, imported, userId, warnings);
        return host;
    }

    /// <summary>Applies the file's details to a host, keeping what the file doesn't mention.</summary>
    /// <returns>Whether anything changed.</returns>
    private bool UpdateHost(Host host, ImportedHost imported, Guid userId, List<string> warnings)
    {
        var changed = false;

        if (!string.Equals(host.Hostname, imported.Hostname, StringComparison.Ordinal) || host.IpAddress != imported.IpAddress)
        {
            host.Hostname = imported.Hostname;
            host.IpAddress = imported.IpAddress;
            // A new address may be a different machine: its keys and certificates must be confirmed again.
            host.TrustedKeys.Clear();
            changed = true;
        }

        if (imported.Description is { } description && host.Description != description)
        {
            host.Description = description;
            changed = true;
        }

        if (imported.MacAddress is { } mac && host.MacAddress != mac)
        {
            host.MacAddress = mac;
            changed = true;
        }

        foreach (var service in imported.Services)
        {
            var port = DefaultPorts.TryGetValue(service.Protocol, out var defaultPort) && service.Port == defaultPort ? null : service.Port;
            var existing = host.Services.FirstOrDefault(s =>
                s.Protocol == service.Protocol
                && (service.Protocol != ServiceProtocol.Other || string.Equals(s.Label, service.Label, StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
            {
                if (host.Services.Count >= MaxServices)
                {
                    warnings.Add($"{host.Name}: it already has {MaxServices} services, so {service.Protocol} wasn't added.");
                    continue;
                }

                host.Services.Add(new HostService
                {
                    Protocol = service.Protocol,
                    Port = port,
                    Label = service.Label,
                    Position = host.Services.Count == 0 ? 0 : host.Services.Max(s => s.Position) + 1,
                });
                changed = true;
            }
            else if (existing.Port != port)
            {
                existing.Port = port;
                changed = true;
            }
        }

        foreach (var login in imported.Credentials)
        {
            var existing = host.Credentials.FirstOrDefault(c =>
                login.Username is not null
                    ? string.Equals(c.Username, login.Username, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.Domain, login.Domain, StringComparison.OrdinalIgnoreCase)
                    : c.Username is null && c.Label == ImportedHost.PasswordOnlyLabel);
            if (existing is null)
            {
                if (host.Credentials.Count >= MaxCredentials)
                {
                    warnings.Add($"{host.Name}: it already has {MaxCredentials} credentials, so {login.Username ?? "a password"} wasn't added.");
                    continue;
                }

                host.Credentials.Add(new HostCredential
                {
                    Label = login.Username is null ? ImportedHost.PasswordOnlyLabel : null,
                    Username = login.Username,
                    Domain = login.Domain,
                    ProtectedPassword = login.Password is null ? null : protector.Protect(login.Password),
                    Position = host.Credentials.Count == 0 ? 0 : host.Credentials.Max(c => c.Position) + 1,
                });
                changed = true;
            }
            else if (login.Password is not null
                && (existing.ProtectedPassword is null || protector.Unprotect(existing.ProtectedPassword) != login.Password))
            {
                existing.ProtectedPassword = protector.Protect(login.Password);
                changed = true;
            }
        }

        // The folder tags follow the file: moved connections move, and top-level ones lose theirs.
        var folders = host.Tags.Where(t => t.Name.Equals(FolderTag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!folders.Select(t => t.Value).Order(StringComparer.Ordinal).SequenceEqual(imported.Folders.Order(StringComparer.Ordinal)))
        {
            host.Tags.RemoveAll(folders.Contains);
            host.Tags.AddRange(imported.Folders.Select(folder => new HostTag { Name = FolderTag, Value = folder }));
            changed = true;
        }

        if (changed)
        {
            host.UpdatedAtUtc = timeProvider.GetUtcNow();
            host.UpdatedByUserId = userId;
        }

        return changed;
    }

    /// <summary>A host as the file describes it: one or more same-name connections to one address.</summary>
    private sealed class ImportedHost(string name, string? hostname, string? ipAddress)
    {
        /// <summary>The label of a credential that has a password but no username (e.g. for VNC).</summary>
        public const string PasswordOnlyLabel = "mRemoteNG";

        public string Name { get; } = name;

        public string? Hostname { get; } = hostname;

        public string? IpAddress { get; } = ipAddress;

        public string? Description { get; private set; }

        public string? MacAddress { get; private set; }

        public List<string> Folders { get; } = [];

        public List<(ServiceProtocol Protocol, int? Port, string? Label)> Services { get; } = [];

        public List<(string? Username, string? Domain, string? Password)> Credentials { get; } = [];

        public void Merge(MRemoteNgConnection connection, List<string> warnings)
        {
            if (Description is null && connection.Description is { } description)
            {
                Description = description.Length > 4000 ? description[..4000] : description;
            }

            if (MacAddress is null && connection.MacAddress is { } mac)
            {
                if (NetworkAddress.TryNormalizeMac(mac, out var normalized))
                {
                    MacAddress = normalized;
                }
                else
                {
                    warnings.Add($"{Name}: the MAC address “{mac}” isn't valid, so it was left out.");
                }
            }

            if (connection.FolderPath is { } folder && !Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                if (folder.Length <= HostTag.ValueMaxLength)
                {
                    Folders.Add(folder);
                }
                else
                {
                    warnings.Add($"{Name}: its folder path is longer than {HostTag.ValueMaxLength} characters, so it has no folder tag.");
                }
            }

            var label = connection.ProtocolLabel is { Length: > 100 } longLabel ? longLabel[..100] : connection.ProtocolLabel;
            if (!Services.Any(s => s.Protocol == connection.Protocol && string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase)))
            {
                Services.Add((connection.Protocol, connection.Port, label));
            }

            if (connection.Username is null && connection.Password is null)
            {
                return;
            }

            if (connection.Username is { Length: > 256 } || connection.Domain is { Length: > 256 } || connection.Password is { Length: > 1024 })
            {
                warnings.Add($"{Name}: its username, domain or password is too long, so its login was left out.");
                return;
            }

            var index = Credentials.FindIndex(c =>
                string.Equals(c.Username, connection.Username, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Domain, connection.Domain, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                Credentials.Add((connection.Username, connection.Domain, connection.Password));
            }
            else if (Credentials[index].Password is null && connection.Password is not null)
            {
                Credentials[index] = Credentials[index] with { Password = connection.Password };
            }
        }
    }
}
