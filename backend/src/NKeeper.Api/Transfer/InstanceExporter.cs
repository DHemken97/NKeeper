using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Transfer;

/// <summary>
/// Writes everything a new instance needs (users, access grants, hosts with their services, tags,
/// credentials, trusted keys and hardware) into an encrypted bundle. Credential secrets are decrypted
/// with this instance's keys and re-encrypted under the passphrase; this instance's Data Protection
/// keys and sign-in sessions are never included.
/// </summary>
internal sealed class InstanceExporter(NKeeperDbContext db, CredentialProtector protector, TimeProvider timeProvider, ILogger<InstanceExporter> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    /// <summary>The running NKeeper's version, e.g. "1.0.0+abc123".</summary>
    public static string Version =>
        typeof(InstanceExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <returns>The bundle file's bytes.</returns>
    public async Task<byte[]> ExportAsync(string exportedBy, Guid exportedById, string passphrase, CancellationToken cancellationToken, int iterations = BundleFormat.DefaultIterations)
    {
        var bundle = await ReadAsync(cancellationToken);
        var migration = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Last();

        // Serialized and compressed in memory, encrypted, then wiped: the plain secrets don't outlive the request.
        using var plain = new MemoryStream();
        await using (var gzip = new GZipStream(plain, CompressionLevel.Optimal, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, bundle, Json, cancellationToken);
        }

        var buffer = plain.GetBuffer();
        try
        {
            using var output = new MemoryStream();
            var header = new BundleHeader(BundleFormat.Version, Version, migration, timeProvider.GetUtcNow(), exportedBy, "", 0, "", "", 0);
            await BundleFormat.WriteAsync(output, header, passphrase, buffer.AsMemory(0, (int)plain.Length), cancellationToken, iterations);
            logger.LogWarning(
                "Instance data exported by user {UserId}: {Users} users, {Hosts} hosts, {Credentials} credentials",
                exportedById, bundle.Users.Count, bundle.Hosts.Count, bundle.Hosts.Sum(h => h.Credentials.Count));
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<BundleV1> ReadAsync(CancellationToken cancellationToken)
    {
        var roles = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);
        var rolesByUser = roles.ToLookup(r => r.UserId, r => r.Name!);

        var users = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAtUtc).ToListAsync(cancellationToken);
        var grants = await db.HostAccessGrants.AsNoTracking().ToListAsync(cancellationToken);
        var email = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var hosts = await db.Hosts.AsNoTracking()
            .Include(h => h.Services)
            .Include(h => h.Tags)
            .Include(h => h.Credentials)
            .Include(h => h.TrustedKeys)
            .AsSplitQuery()
            .OrderBy(h => h.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return new BundleV1(
            [.. users.Select(u => new BundleUser(
                u.Id, u.UserName!, u.Email, u.EmailConfirmed, u.PasswordHash, u.SecurityStamp, u.LockoutEnabled, u.LockoutEnd,
                u.AccessFailedCount, u.IsDisabled, u.MustChangePassword, u.CreatedAtUtc, [.. rolesByUser[u.Id]], u.IsDirectoryAccount,
                u.WorkspaceGroupBy, u.WorkspaceSort, u.SingleSignOnSubject, u.TwoFactorEnabled))],
            [.. grants.Select(g => new BundleGrant(g.Id, g.UserId, g.HostId, g.TagName, g.TagValue, g.Level, g.CreatedAtUtc, g.CreatedByUserId))],
            [.. hosts.Select(h => new BundleHost(
                h.Id, h.Name, h.Hostname, h.IpAddress, h.MacAddress, h.Description, h.IsVirtualMachine, h.HypervisorId, h.VirtualMachineId,
                h.HypervisorSeenAtUtc, h.CpuCores, h.MemoryMiB, h.SerialNumber, h.CreatedAtUtc, h.UpdatedAtUtc, h.CreatedByUserId, h.UpdatedByUserId,
                [.. h.Services.OrderBy(s => s.Position).Select(s => new BundleService(s.Id, s.Protocol, s.Port, s.Label, s.Position, s.RdpSecurity, s.Address))],
                [.. h.Tags.Select(t => new BundleTag(t.Id, t.Name, t.Value, t.Inheritable))],
                [.. h.Credentials.OrderBy(c => c.Position).Select(c => new BundleCredential(
                    c.Id, c.Label, c.Username, c.Domain,
                    c.ProtectedPassword is null ? null : protector.Unprotect(c.ProtectedPassword),
                    c.ProtectedPrivateKey is null ? null : protector.Unprotect(c.ProtectedPrivateKey),
                    c.Notes, c.Position))],
                [.. h.TrustedKeys.Select(k => new BundleTrustedKey(k.Id, k.Protocol, k.Port, k.Algorithm, k.Fingerprint, k.TrustedAtUtc, k.TrustedByUserId))],
                h.JumpHostId,
                h.JumpCredentialId))],
            email is null
                ? null
                : new BundleEmail(
                    email.Enabled, email.Host, email.Port, email.Security, email.UserName,
                    email.ProtectedPassword is null ? null : protector.Unprotect(email.ProtectedPassword),
                    email.FromAddress, email.FromName));
    }
}

/// <summary>Whether hosts and users are locked because this instance's data was exported to move it.</summary>
internal sealed class InstanceLock(NKeeperDbContext db, TimeProvider timeProvider)
{
    public Task<InstanceSettings?> GetAsync(CancellationToken cancellationToken) =>
        db.InstanceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == InstanceSettings.SingletonId, cancellationToken);

    public async Task<bool> IsLockedAsync(CancellationToken cancellationToken) => (await GetAsync(cancellationToken))?.IsLocked == true;

    public async Task SetAsync(bool locked, Guid userId, CancellationToken cancellationToken)
    {
        var settings = await db.InstanceSettings.FirstOrDefaultAsync(s => s.Id == InstanceSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = new InstanceSettings();
            db.InstanceSettings.Add(settings);
        }

        settings.IsLocked = locked;
        settings.LockedAtUtc = locked ? timeProvider.GetUtcNow() : null;
        settings.LockedByUserId = locked ? userId : null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
