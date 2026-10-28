namespace NKeeper.Sdk.Plugins;

/// <summary>
/// A plugin's own settings, kept in NKeeper's database: plain values, and secrets (API tokens,
/// passwords) encrypted with the server's keys, never returned to browsers. They move with the
/// instance to a new server. Inject it as <c>[FromKeyedServices("{id}")] IPluginSettings</c>.
/// </summary>
public interface IPluginSettings
{
    /// <summary>Every saved value (not secrets), which secrets are set, and when they were last saved.</summary>
    Task<PluginSettingsSnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>A secret, decrypted; null when it isn't set.</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Saves values and secrets together. A null value removes the setting; names left out are
    /// kept. Records who saved them (the signed-in user) and when.
    /// </summary>
    Task SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? secrets,
        CancellationToken cancellationToken);
}

/// <summary>A plugin's saved settings.</summary>
public sealed record PluginSettingsSnapshot(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> Secrets,
    DateTimeOffset? UpdatedAtUtc)
{
    public static readonly PluginSettingsSnapshot Empty = new(new Dictionary<string, string>(), new HashSet<string>(), null);

    /// <summary>Nothing has been saved yet.</summary>
    public bool IsEmpty => Values.Count == 0 && Secrets.Count == 0;

    public string? Get(string name) => Values.TryGetValue(name, out var value) ? value : null;

    public bool GetBool(string name) => bool.TryParse(Get(name), out var value) && value;

    public bool HasSecret(string name) => Secrets.Contains(name);
}

/// <summary>
/// The per-host values a plugin declared with <see cref="PluginContext.AddHostField"/>. This
/// doesn't check access: check the user's access first with <c>HostAccess</c>, or use
/// <see cref="Inventory.IHostDirectory"/>, which does. Inject it as
/// <c>[FromKeyedServices("{id}")] IHostPluginData</c>.
/// </summary>
public interface IHostPluginData
{
    Task<string?> GetAsync(Guid hostId, string field, CancellationToken cancellationToken);

    /// <summary>Every host's value of a field, by host ID.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAllAsync(string field, CancellationToken cancellationToken);

    /// <summary>Sets (or, with null or blank, removes) a host's value, validated against the field's declaration.</summary>
    /// <exception cref="Inventory.InventoryValidationException">The value doesn't fit the field.</exception>
    Task SetAsync(Guid hostId, string field, string? value, CancellationToken cancellationToken);
}
