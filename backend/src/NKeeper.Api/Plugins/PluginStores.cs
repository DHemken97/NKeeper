using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Plugins;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Plugins;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Plugins;

/// <summary>A plugin's settings in the PluginSettings table, secrets encrypted with the credential keys.</summary>
internal sealed class PluginSettingsStore(
    string pluginId,
    NKeeperDbContext db,
    CredentialProtector protector,
    IHttpContextAccessor httpContext,
    TimeProvider timeProvider) : IPluginSettings
{
    public async Task<PluginSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var rows = await db.PluginSettings.AsNoTracking().Where(s => s.PluginId == pluginId).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return PluginSettingsSnapshot.Empty;
        }

        return new PluginSettingsSnapshot(
            rows.Where(r => r.Value is not null).ToDictionary(r => r.Name, r => r.Value!, StringComparer.Ordinal),
            rows.Where(r => r.ProtectedValue is not null).Select(r => r.Name).ToHashSet(StringComparer.Ordinal),
            rows.Max(r => r.UpdatedAtUtc));
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        var secret = await db.PluginSettings.AsNoTracking()
            .Where(s => s.PluginId == pluginId && s.Name == name)
            .Select(s => s.ProtectedValue)
            .FirstOrDefaultAsync(cancellationToken);
        return secret is null ? null : protector.Unprotect(secret);
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? secrets,
        CancellationToken cancellationToken)
    {
        var names = values.Keys.Concat(secrets?.Keys ?? []).ToList();
        if (names.Any(n => n.Length is 0 or > PluginSetting.NameMaxLength) || names.Count != names.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Setting names must be 1 to 100 characters, and each saved once.", nameof(values));
        }

        var rows = await db.PluginSettings.Where(s => s.PluginId == pluginId && names.Contains(s.Name)).ToDictionaryAsync(s => s.Name, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var userId = httpContext.HttpContext?.User.Identity?.IsAuthenticated == true ? httpContext.HttpContext.User.GetUserId() : (Guid?)null;

        void Set(string name, string? value, byte[]? protectedValue)
        {
            if (value is null && protectedValue is null)
            {
                if (rows.TryGetValue(name, out var removed))
                {
                    db.PluginSettings.Remove(removed);
                }

                return;
            }

            if (!rows.TryGetValue(name, out var row))
            {
                row = new PluginSetting { PluginId = pluginId, Name = name };
                db.PluginSettings.Add(row);
            }

            row.Value = value;
            row.ProtectedValue = protectedValue;
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = userId;
        }

        foreach (var (name, value) in values)
        {
            Set(name, value, null);
        }

        foreach (var (name, secret) in secrets ?? new Dictionary<string, string?>())
        {
            Set(name, null, secret is null ? null : protector.Protect(secret));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A plugin's per-host values in the HostPluginValues table.</summary>
internal sealed class HostPluginDataStore(string pluginId, NKeeperDbContext db, HostFieldRegistry fields) : IHostPluginData
{
    public Task<string?> GetAsync(Guid hostId, string field, CancellationToken cancellationToken) =>
        db.HostPluginValues.AsNoTracking()
            .Where(v => v.HostId == hostId && v.PluginId == pluginId && v.Name == field)
            .Select(v => v.Value)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetAllAsync(string field, CancellationToken cancellationToken) =>
        await db.HostPluginValues.AsNoTracking()
            .Where(v => v.PluginId == pluginId && v.Name == field)
            .ToDictionaryAsync(v => v.HostId, v => v.Value, cancellationToken);

    public async Task SetAsync(Guid hostId, string field, string? value, CancellationToken cancellationToken)
    {
        var normalized = fields.Normalize(pluginId, field, value);
        var row = await db.HostPluginValues.FirstOrDefaultAsync(v => v.HostId == hostId && v.PluginId == pluginId && v.Name == field, cancellationToken);
        if (normalized is null)
        {
            if (row is not null)
            {
                db.HostPluginValues.Remove(row);
            }
        }
        else if (row is null)
        {
            if (!await db.Hosts.AnyAsync(h => h.Id == hostId, cancellationToken))
            {
                throw new InventoryValidationException("hostId", "Host not found.");
            }

            db.HostPluginValues.Add(new HostPluginValue { HostId = hostId, PluginId = pluginId, Name = field, Value = normalized });
        }
        else
        {
            row.Value = normalized;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
