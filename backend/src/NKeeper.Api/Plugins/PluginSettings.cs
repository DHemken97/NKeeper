namespace NKeeper.Api.Plugins;

/// <summary>Where plugins come from and which signatures are trusted (the <c>Plugins</c> section).</summary>
public sealed class PluginsSettings
{
    public const string SectionName = "Plugins";

    /// <summary>Directories with <c>.nkplugin</c> packages, relative to the server's own directory. The server's own plugins are in "plugins".</summary>
    public string[] Directories { get; set; } = ["plugins"];

    /// <summary>
    /// Ed25519 public keys (base64) whose signatures are trusted, besides the <c>trusted-keys/*.pub</c>
    /// files shipped with the server.
    /// </summary>
    public string[] TrustedKeys { get; set; } = [];

    /// <summary>Plugin IDs not to load, though their packages are present.</summary>
    public string[] Disabled { get; set; } = [];

    /// <summary>Where packages are extracted to be loaded; by default a directory under the system's temporary directory.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Plugin assemblies that are part of the server's own files (its directory and dependency
    /// list), loaded without a package. For development and tests: they're as trusted as the server's
    /// own files, so they need no signature.
    /// </summary>
    public string[] BuiltIn { get; set; } = [];
}
