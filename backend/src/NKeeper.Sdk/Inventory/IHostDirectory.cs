using NKeeper.Sdk.Access;

namespace NKeeper.Sdk.Inventory;

/// <summary>
/// NKeeper's hosts as plugins see them: read, created and changed with the same checks, access
/// rules and responses as the web app's own host API.
/// </summary>
public interface IHostDirectory
{
    /// <summary>The hosts with these IDs that the user can see, ordered by name.</summary>
    Task<IReadOnlyList<HostResponse>> GetAsync(IReadOnlyCollection<Guid> ids, RequestUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Sets one of a plugin's per-host fields on hosts the user may manage; a null or blank value
    /// removes it. Hosts whose value changes get a new update time.
    /// </summary>
    /// <returns>The hosts, as they are now.</returns>
    /// <exception cref="Access.HostAccessDeniedException">A host is missing or the user can't manage it.</exception>
    /// <exception cref="InventoryValidationException">The field isn't declared, or a value doesn't fit it.</exception>
    Task<IReadOnlyList<HostResponse>> SetPluginValuesAsync(
        string pluginId, string field, IReadOnlyDictionary<Guid, string?> values, RequestUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Adds hosts from another system's data, leniently: text too long is shortened, tags that
    /// aren't valid are left out. Only administrators may add hosts: check before calling.
    /// </summary>
    /// <exception cref="InventoryValidationException">A host has neither a hostname nor an IP address, or a plugin value doesn't fit its field.</exception>
    Task<IReadOnlyList<HostResponse>> CreateAsync(IReadOnlyList<NewHost> hosts, RequestUser user, CancellationToken cancellationToken);
}

/// <summary>A host to add with <see cref="IHostDirectory.CreateAsync"/>. It needs a hostname or an IP address.</summary>
public sealed record NewHost(string Name)
{
    public string? Hostname { get; init; }

    public string? IpAddress { get; init; }

    public string? MacAddress { get; init; }

    public string? Description { get; init; }

    public string? SerialNumber { get; init; }

    public IReadOnlyList<(string Name, string? Value)> Tags { get; init; } = [];

    /// <summary>Per-host plugin values, by plugin ID then field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Plugins { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
