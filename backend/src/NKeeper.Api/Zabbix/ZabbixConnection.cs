using Microsoft.EntityFrameworkCore;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Zabbix;

namespace NKeeper.Api.Zabbix;

/// <summary>Opens the Zabbix API with the saved settings: their address, the decrypted API token and the trusted certificate.</summary>
public sealed class ZabbixConnection(NKeeperDbContext db, CredentialProtector protector)
{
    public Task<ZabbixSettings?> GetSettingsAsync(CancellationToken cancellationToken) =>
        db.ZabbixSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ZabbixSettings.SingletonId, cancellationToken);

    /// <summary>The Zabbix frontend's address when Zabbix is set up and on; null otherwise.</summary>
    public async Task<Uri?> GetFrontendAsync(CancellationToken cancellationToken) =>
        await GetSettingsAsync(cancellationToken) is { Enabled: true, ProtectedApiToken: not null } settings
        && ZabbixUrl.TryNormalize(settings.Url, out var frontend)
            ? frontend
            : null;

    /// <exception cref="SessionException">With <see cref="ZabbixErrorCodes.Disabled"/> when Zabbix isn't set up or is off.</exception>
    internal async Task<ZabbixClient> OpenAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (settings is not { Enabled: true, ProtectedApiToken: { } token } || !ZabbixUrl.TryNormalize(settings.Url, out var frontend))
        {
            throw new SessionException(ZabbixErrorCodes.Disabled, "Zabbix isn't set up. An administrator can set it up under Settings.");
        }

        return new ZabbixClient(ZabbixUrl.ApiEndpoint(frontend), protector.Unprotect(token), settings.TrustedCertificateFingerprint);
    }
}
