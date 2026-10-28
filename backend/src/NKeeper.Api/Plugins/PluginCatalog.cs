using System.Reflection;
using NKeeper.Plugins.Packaging;
using NKeeper.Sdk.Plugins;

namespace NKeeper.Api.Plugins;

public enum PluginStatus
{
    Loaded,
    Disabled,
    Failed,
}

/// <summary>A plugin package the server found, and what became of it.</summary>
public sealed class PluginEntry
{
    public required PluginManifest Manifest { get; init; }

    /// <summary>The package file; null for a built-in plugin assembly.</summary>
    public string? Source { get; init; }

    /// <summary>The key the package is signed with; null for a built-in plugin.</summary>
    public string? SignerKeyId { get; init; }

    /// <summary>SHA-256 of the package, which also names its extracted directory and versions its browser files.</summary>
    public string? PackageHash { get; init; }

    public PluginStatus Status { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>Where the package was extracted to.</summary>
    public string? Directory { get; internal set; }

    public Assembly? Assembly { get; internal set; }

    public INKeeperPlugin? Instance { get; internal set; }

    public PluginContext? Context { get; internal set; }

    internal PluginLoadContext? LoadContext { get; set; }

    internal VerifiedPluginPackage? Package { get; init; }

    public string Id => Manifest.Id;

    public void Fail(string error)
    {
        Status = PluginStatus.Failed;
        Error = error;
    }
}

/// <summary>A package that couldn't be read or trusted at all (so it has no manifest to show).</summary>
public sealed record RejectedPackage(string Source, string Error);

/// <summary>Every plugin the server found at start-up: loaded, turned off or failed, and the packages it refused.</summary>
public sealed class PluginCatalog(IReadOnlyList<PluginEntry> plugins, IReadOnlyList<RejectedPackage> rejected, IReadOnlyCollection<string> trustedKeyIds)
{
    public static readonly PluginCatalog Empty = new([], [], []);

    public IReadOnlyList<PluginEntry> Plugins { get; } = plugins;

    public IReadOnlyList<RejectedPackage> Rejected { get; } = rejected;

    public IReadOnlyCollection<string> TrustedKeyIds { get; } = trustedKeyIds;

    public IEnumerable<PluginEntry> Loaded => Plugins.Where(p => p.Status == PluginStatus.Loaded);

    public PluginEntry? Find(string id) => Plugins.FirstOrDefault(p => p.Id == id && p.Status == PluginStatus.Loaded);

    public bool IsLoaded(string id) => Find(id) is not null;

    /// <summary>The loaded plugin a type belongs to, by its assembly.</summary>
    public PluginEntry? ForAssembly(Assembly assembly) => Loaded.FirstOrDefault(p => p.Assembly == assembly);
}
