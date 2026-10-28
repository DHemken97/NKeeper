using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Plugins;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Transfer;

/// <summary>A decrypted, validated bundle, ready to preview or import.</summary>
internal sealed record OpenedBundle(BundleHeader Header, BundleV1 Data, IReadOnlyList<string> Warnings);

/// <summary>
/// Brings an exported instance into this one. Only a fresh instance accepts it (no hosts, and no
/// users but the administrator importing), so an import can never overwrite a live inventory. The
/// whole import is one transaction; secrets are re-encrypted with this instance's own keys.
/// </summary>
internal sealed class InstanceImporter(
    NKeeperDbContext db,
    CredentialProtector protector,
    ILookupNormalizer normalizer,
    TimeProvider timeProvider,
    ILogger<InstanceImporter> logger)
{
    /// <summary>Decompressed data larger than this is refused (a bundle is at most 256 MB compressed).</summary>
    private const long MaxDataBytes = 1L << 30;

    /// <summary>Why this instance can't receive an import, or null when it can.</summary>
    public async Task<string?> BlockedReasonAsync(Guid callerId, CancellationToken cancellationToken)
    {
        if (await db.Hosts.AnyAsync(cancellationToken))
        {
            return "This instance already has hosts. Imports only go into a new, empty NKeeper, so nothing here is overwritten.";
        }

        if (await db.Users.AnyAsync(u => u.Id != callerId, cancellationToken))
        {
            return "This instance already has other user accounts. Imports only go into a new NKeeper whose only account is the administrator importing.";
        }

        return null;
    }

    /// <summary>Decrypts and checks a bundle without changing anything.</summary>
    /// <exception cref="BundleException">It can't be read, or it doesn't fit this version of NKeeper.</exception>
    public OpenedBundle Open(byte[] file, string passphrase)
    {
        var (header, compressed) = BundleFormat.Read(file, passphrase);
        if (!db.Database.GetMigrations().Contains(header.Migration, StringComparer.Ordinal))
        {
            throw new BundleException(
                $"The file comes from a newer NKeeper ({header.NKeeperVersion}) than this one ({InstanceExporter.Version}). Update this server first, then import again.");
        }

        BundleV1 data;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var limited = new LimitedStream(gzip, MaxDataBytes);
            data = JsonSerializer.Deserialize<BundleV1>(limited, InstanceExporter.Json)
                ?? throw new BundleException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new BundleException("The file's contents can't be read. It may come from a different version of NKeeper.");
        }
        finally
        {
            Array.Clear(compressed);
        }

        return new OpenedBundle(header, data, Validate(data));
    }

    public static ImportSummaryResponse Summarize(OpenedBundle bundle, bool imported) => new(
        bundle.Header.ExportedBy,
        bundle.Header.ExportedAtUtc,
        bundle.Header.NKeeperVersion,
        bundle.Data.Users.Count,
        bundle.Data.Users.Count(u => u.Roles.Contains(SystemRoles.Administrator) && !u.IsDisabled),
        bundle.Data.Grants.Count,
        bundle.Data.Hosts.Count,
        bundle.Data.Hosts.Sum(h => h.Credentials.Count),
        bundle.Data.Hosts.Sum(h => h.TrustedKeys.Count),
        bundle.Warnings,
        imported);

    /// <summary>Replaces this fresh instance's data with the bundle's, in one transaction. The caller's account is replaced too.</summary>
    /// <exception cref="InvalidOperationException">The instance isn't fresh any more.</exception>
    public async Task ImportAsync(OpenedBundle bundle, Guid callerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await BlockedReasonAsync(callerId, cancellationToken) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        // The administrator who set up this instance is replaced by the imported accounts (their
        // sessions and roles go with it); they sign in again with their account from the old server.
        await db.Users.Where(u => u.Id == callerId).ExecuteDeleteAsync(cancellationToken);

        if (bundle.Data.Email is { } email)
        {
            await db.EmailSettings.ExecuteDeleteAsync(cancellationToken);
            db.EmailSettings.Add(new EmailSettings
            {
                Enabled = email.Enabled,
                Host = email.Host,
                Port = email.Port,
                Security = email.Security,
                UserName = email.UserName,
                ProtectedPassword = email.Password is null ? null : protector.Protect(email.Password),
                FromAddress = email.FromAddress,
                FromName = email.FromName,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
            });
        }

        var pluginSettings = PluginSettingsOf(bundle.Data);
        if (pluginSettings.Count > 0)
        {
            await db.PluginSettings.ExecuteDeleteAsync(cancellationToken);
            db.PluginSettings.AddRange(pluginSettings.Select(s => new PluginSetting
            {
                PluginId = s.PluginId,
                Name = s.Name,
                Value = s.Secret is null ? s.Value : null,
                // Re-encrypted with this instance's own keys.
                ProtectedValue = s.Secret is null ? null : protector.Protect(s.Secret),
                UpdatedAtUtc = s.UpdatedAtUtc,
            }));
        }

        var roles = await db.Roles.ToDictionaryAsync(r => r.NormalizedName!, cancellationToken);
        foreach (var user in bundle.Data.Users)
        {
            db.Users.Add(new ApplicationUser
            {
                Id = user.Id,
                UserName = user.UserName,
                NormalizedUserName = normalizer.NormalizeName(user.UserName),
                Email = user.Email,
                NormalizedEmail = user.Email is null ? null : normalizer.NormalizeEmail(user.Email),
                EmailConfirmed = user.EmailConfirmed,
                PasswordHash = user.PasswordHash,
                SecurityStamp = user.SecurityStamp ?? Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                LockoutEnabled = user.LockoutEnabled,
                LockoutEnd = user.LockoutEnd,
                AccessFailedCount = user.AccessFailedCount,
                IsDisabled = user.IsDisabled,
                MustChangePassword = user.MustChangePassword,
                IsDirectoryAccount = user.IsDirectoryAccount,
                SingleSignOnSubject = user.SingleSignOnSubject,
                TwoFactorEnabled = user.EmailTwoFactor,
                WorkspaceGroupBy = [.. user.WorkspaceGroupBy ?? []],
                WorkspaceSort = user.WorkspaceSort ?? "name",
                CreatedAtUtc = user.CreatedAtUtc,
            });

            foreach (var name in user.Roles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = normalizer.NormalizeName(name);
                if (!roles.TryGetValue(normalized, out var role))
                {
                    role = new ApplicationRole(name) { Id = Guid.NewGuid(), NormalizedName = normalized, ConcurrencyStamp = Guid.NewGuid().ToString() };
                    db.Roles.Add(role);
                    roles[normalized] = role;
                }

                db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            }
        }

        foreach (var host in bundle.Data.Hosts)
        {
            db.Hosts.Add(new Host
            {
                Id = host.Id,
                Name = host.Name,
                Hostname = host.Hostname,
                IpAddress = host.IpAddress,
                MacAddress = host.MacAddress,
                Description = host.Description,
                IsVirtualMachine = host.IsVirtualMachine,
                HypervisorId = host.HypervisorId,
                VirtualMachineId = host.VirtualMachineId,
                HypervisorSeenAtUtc = host.HypervisorSeenAtUtc,
                CpuCores = host.CpuCores,
                MemoryMiB = host.MemoryMiB,
                SerialNumber = host.SerialNumber,
                JumpHostId = host.JumpHostId,
                JumpCredentialId = host.JumpCredentialId,
                PluginValues = PluginValuesOf(host),
                CreatedAtUtc = host.CreatedAtUtc,
                UpdatedAtUtc = host.UpdatedAtUtc,
                CreatedByUserId = host.CreatedByUserId,
                UpdatedByUserId = host.UpdatedByUserId,
                Services = [.. host.Services.Select(s => new HostService
                {
                    Id = s.Id, Protocol = s.Protocol, Port = s.Port, Label = s.Label, Position = s.Position, RdpSecurity = s.RdpSecurity, Address = s.Address,
                })],
                Tags = [.. host.Tags.Select(t => new HostTag { Id = t.Id, Name = t.Name, Value = t.Value })],
                Credentials = [.. host.Credentials.Select(c => new HostCredential
                {
                    Id = c.Id,
                    Label = c.Label,
                    Username = c.Username,
                    Domain = c.Domain,
                    Notes = c.Notes,
                    Position = c.Position,
                    // Re-encrypted with this instance's own keys.
                    ProtectedPassword = c.Password is null ? null : protector.Protect(c.Password),
                    ProtectedPrivateKey = c.PrivateKey is null ? null : protector.Protect(c.PrivateKey),
                })],
                TrustedKeys = [.. host.TrustedKeys.Select(k => new HostTrustedKey
                {
                    Id = k.Id, Protocol = k.Protocol, Port = k.Port, Algorithm = k.Algorithm, Fingerprint = k.Fingerprint, TrustedAtUtc = k.TrustedAtUtc, TrustedByUserId = k.TrustedByUserId,
                })],
            });
        }

        db.HostAccessGrants.AddRange(bundle.Data.Grants.Select(g => new HostAccessGrant
        {
            Id = g.Id, UserId = g.UserId, HostId = g.HostId, TagName = g.TagName, TagValue = g.TagValue, Level = g.Level, CreatedAtUtc = g.CreatedAtUtc, CreatedByUserId = g.CreatedByUserId,
        }));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        logger.LogWarning(
            "Instance data imported by user {UserId} (exported by {ExportedBy} at {ExportedAt} from NKeeper {Version}): {Users} users, {Hosts} hosts",
            callerId, bundle.Header.ExportedBy, bundle.Header.ExportedAtUtc, bundle.Header.NKeeperVersion, bundle.Data.Users.Count, bundle.Data.Hosts.Count);
    }

    /// <summary>
    /// Checks the bundle holds together before anything is written: references point at what's in it,
    /// ids are unique, and someone will be able to administer the new instance.
    /// </summary>
    private static List<string> Validate(BundleV1 data)
    {
        var users = data.Users.Select(u => u.Id).ToHashSet();
        var hosts = data.Hosts.Select(h => h.Id).ToHashSet();
        if (users.Count != data.Users.Count || hosts.Count != data.Hosts.Count
            || data.Users.Select(u => u.UserName.ToUpperInvariant()).Distinct().Count() != data.Users.Count)
        {
            throw new BundleException("The file lists the same user or host twice, so it wasn't imported.");
        }

        var credentialsByHost = data.Hosts.ToDictionary(h => h.Id, h => h.Credentials.Select(c => c.Id).ToHashSet());
        if (data.Hosts.Any(h => h.HypervisorId is { } id && !hosts.Contains(id))
            || data.Hosts.Any(h => h.JumpHostId is { } jump && (!hosts.Contains(jump) || (h.JumpCredentialId is { } c && !credentialsByHost[jump].Contains(c))))
            || data.Grants.Any(g => !users.Contains(g.UserId) || (g.HostId is { } id && !hosts.Contains(id))))
        {
            throw new BundleException("The file refers to users or hosts it doesn't contain, so it wasn't imported.");
        }

        // Domain and single sign-on accounts have no local password: they sign in elsewhere.
        if (!data.Users.Any(u => u.Roles.Contains(SystemRoles.Administrator) && !u.IsDisabled
            && (u.PasswordHash is not null || u.IsDirectoryAccount || u.SingleSignOnSubject is not null)))
        {
            throw new BundleException("The file has no enabled administrator who can sign in, so nobody could manage the new instance.");
        }

        if (PluginSettingsOf(data).Any(s => !Fits(s.PluginId, s.Name))
            || data.Hosts.Any(h => (h.Plugins ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
                .Any(p => p.Value.Any(v => !Fits(p.Key, v.Key) || v.Value.Length > HostPluginValue.MaxLength))))
        {
            throw new BundleException("The file has plugin data that doesn't fit, so it wasn't imported.");
        }

        var warnings = new List<string>();
        var noPassword = data.Users.Count(u => u.PasswordHash is null && !u.IsDirectoryAccount && u.SingleSignOnSubject is null);
        if (noPassword > 0)
        {
            warnings.Add($"{noPassword} account{(noPassword == 1 ? " has" : "s have")} no password; an administrator must set one before they can sign in.");
        }

        var directory = data.Users.Count(u => u.IsDirectoryAccount);
        if (directory > 0)
        {
            warnings.Add($"{directory} account{(directory == 1 ? " signs" : "s sign")} in with Active Directory: set up ActiveDirectory__* on this server the same way first, or {(directory == 1 ? "it" : "they")} can't sign in.");
        }

        var twoFactor = data.Users.Count(u => u.EmailTwoFactor);
        if (twoFactor > 0 && data.Email is not { Enabled: true })
        {
            warnings.Add($"{twoFactor} account{(twoFactor == 1 ? " needs" : "s need")} a sign-in code sent by email, but the file has no email settings: set up email on this server first, or {(twoFactor == 1 ? "it" : "they")} can't sign in.");
        }

        var singleSignOn = data.Users.Count(u => u.SingleSignOnSubject is not null);
        if (singleSignOn > 0)
        {
            warnings.Add($"{singleSignOn} account{(singleSignOn == 1 ? " signs" : "s sign")} in with single sign-on: set up SingleSignOn__* on this server with the same identity provider first, or {(singleSignOn == 1 ? "it" : "they")} can't sign in.");
        }

        var locked = data.Users.Count(u => u.LockoutEnd > DateTimeOffset.UtcNow);
        if (locked > 0)
        {
            warnings.Add($"{locked} account{(locked == 1 ? " is" : "s are")} locked out after failed sign-ins, as on the old server.");
        }

        return warnings;
    }

    private static bool Fits(string pluginId, string name) =>
        pluginId.Length is > 0 and <= PluginSetting.PluginIdMaxLength && name.Length is > 0 and <= PluginSetting.NameMaxLength;

    /// <summary>The plugins' settings, with those of a file written before Zabbix was a plugin as the zabbix plugin's.</summary>
    private static List<BundlePluginSetting> PluginSettingsOf(BundleV1 data)
    {
        var settings = data.PluginSettings?.ToList() ?? [];
        if (data.Zabbix is { } zabbix && !settings.Any(s => s.PluginId == "zabbix"))
        {
            var now = DateTimeOffset.UtcNow;
            settings.Add(new BundlePluginSetting("zabbix", "enabled", zabbix.Enabled ? "true" : "false", null, now));
            if (zabbix.Url.Length > 0)
            {
                settings.Add(new BundlePluginSetting("zabbix", "url", zabbix.Url, null, now));
            }

            if (zabbix.TrustedCertificateFingerprint is { } fingerprint)
            {
                settings.Add(new BundlePluginSetting("zabbix", "trustedCertificateFingerprint", fingerprint, null, now));
            }

            if (zabbix.ApiToken is { } token)
            {
                settings.Add(new BundlePluginSetting("zabbix", "apiToken", null, token, now));
            }
        }

        return settings;
    }

    /// <summary>A host's plugin values, with the Zabbix host ID of a file written before Zabbix was a plugin as the zabbix plugin's hostId.</summary>
    private static List<HostPluginValue> PluginValuesOf(BundleHost host)
    {
        var values = (host.Plugins ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
            .SelectMany(p => p.Value.Select(v => new HostPluginValue { HostId = host.Id, PluginId = p.Key, Name = v.Key, Value = v.Value }))
            .ToList();
        if (host.ZabbixHostId is { } zabbixHostId && !values.Any(v => v.PluginId == "zabbix" && v.Name == "hostId"))
        {
            values.Add(new HostPluginValue { HostId = host.Id, PluginId = "zabbix", Name = "hostId", Value = zabbixHostId });
        }

        return values;
    }

    /// <summary>Stops reading past a size, so a small compressed file can't expand without bound.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _read += read;
            return _read > limit ? throw new InvalidDataException("The data is too large.") : read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
