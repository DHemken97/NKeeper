using System.Text.RegularExpressions;
using NKeeper.Infrastructure.Plugins;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Plugins;

namespace NKeeper.Api.Plugins;

/// <summary>The per-host fields loaded plugins declared, and the checks values must pass to be stored.</summary>
public sealed class HostFieldRegistry(PluginCatalog catalog)
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    public HostField? Find(string pluginId, string name) =>
        catalog.Find(pluginId)?.Context?.HostFields.FirstOrDefault(f => f.Name == name);

    /// <summary>The value to store (trimmed), or null to remove it.</summary>
    /// <exception cref="InventoryValidationException">The field isn't declared by a loaded plugin, or the value doesn't fit it.</exception>
    public string? Normalize(string pluginId, string name, string? value)
    {
        var key = $"plugins.{pluginId}.{name}";
        var field = Find(pluginId, name)
            ?? throw new InventoryValidationException(key, catalog.IsLoaded(pluginId)
                ? $"The {pluginId} plugin keeps no “{name}” on hosts."
                : $"The {pluginId} plugin isn't installed.");
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > Math.Min(field.MaxLength, HostPluginValue.MaxLength)
            || (field.Pattern is { } pattern && !Regex.IsMatch(text, $"^(?:{pattern})$", RegexOptions.CultureInvariant, PatternTimeout)))
        {
            throw new InventoryValidationException(key, field.Message);
        }

        return text;
    }
}
