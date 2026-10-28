using NKeeper.Sdk.Plugins;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// The Zabbix server NKeeper reads host information from, through its JSON-RPC API with an API
/// token, set by administrators on the Settings page. Kept in the plugin's settings; the token is a
/// secret there.
/// </summary>
public sealed record ZabbixSettings(bool Enabled, string Url, bool HasApiToken, string? TrustedCertificateFingerprint, DateTimeOffset? UpdatedAtUtc)
{
    public const int UrlMaxLength = 2048;

    /// <summary>Zabbix IDs are unsigned 64-bit numbers: at most 20 digits.</summary>
    public const int HostIdMaxLength = 20;

    public const string EnabledKey = "enabled";
    public const string UrlKey = "url";
    public const string FingerprintKey = "trustedCertificateFingerprint";
    public const string ApiTokenKey = "apiToken";

    public static ZabbixSettings From(PluginSettingsSnapshot snapshot) => new(
        snapshot.GetBool(EnabledKey),
        snapshot.Get(UrlKey) ?? string.Empty,
        snapshot.HasSecret(ApiTokenKey),
        snapshot.Get(FingerprintKey),
        snapshot.UpdatedAtUtc);
}

/// <summary>Opens the Zabbix API with the saved settings: their address, the decrypted API token and the trusted certificate.</summary>
public sealed class ZabbixConnection([FromKeyedServices(ZabbixPlugin.Id)] IPluginSettings settings)
{
    public async Task<ZabbixSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        ZabbixSettings.From(await settings.GetAsync(cancellationToken));

    public Task<string?> GetApiTokenAsync(CancellationToken cancellationToken) =>
        settings.GetSecretAsync(ZabbixSettings.ApiTokenKey, cancellationToken);

    /// <summary>The Zabbix frontend's address when Zabbix is set up and on; null otherwise.</summary>
    public async Task<Uri?> GetFrontendAsync(CancellationToken cancellationToken) =>
        await GetSettingsAsync(cancellationToken) is { Enabled: true, HasApiToken: true } saved
        && ZabbixUrl.TryNormalize(saved.Url, out var frontend)
            ? frontend
            : null;

    /// <exception cref="SessionException">With <see cref="ZabbixErrorCodes.Disabled"/> when Zabbix isn't set up or is off.</exception>
    internal async Task<ZabbixClient> OpenAsync(CancellationToken cancellationToken)
    {
        var saved = await GetSettingsAsync(cancellationToken);
        if (!saved.Enabled || !ZabbixUrl.TryNormalize(saved.Url, out var frontend) || await GetApiTokenAsync(cancellationToken) is not { } token)
        {
            throw new SessionException(ZabbixErrorCodes.Disabled, "Zabbix isn't set up. An administrator can set it up under Settings.");
        }

        return new ZabbixClient(ZabbixUrl.ApiEndpoint(frontend), token, saved.TrustedCertificateFingerprint);
    }
}
