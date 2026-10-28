using System.Reflection;
using NKeeper.Plugins.Packaging;
using NKeeper.Sdk.Plugins;

namespace NKeeper.Api.Plugins;

/// <summary>
/// Finds, verifies and loads plugins before the server starts. A package is used only when it's
/// signed by a trusted key and unchanged; anything wrong with one plugin fails that plugin (and
/// those that depend on it), never the server.
/// </summary>
internal static class PluginLoader
{
    public static PluginCatalog Load(IConfiguration configuration, string contentRoot)
    {
        var settings = configuration.GetSection(PluginsSettings.SectionName).Get<PluginsSettings>() ?? new PluginsSettings();
        var trusted = TrustedKeys(settings, contentRoot, out var rejected);
        var disabled = settings.Disabled.ToHashSet(StringComparer.Ordinal);
        var serverSdk = SdkVersions.TryParse(PluginSdk.Version, out var sdk) ? sdk : throw new InvalidOperationException("Invalid SDK version.");

        var entries = new List<PluginEntry>();
        foreach (var name in settings.BuiltIn.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            entries.Add(BuiltIn(name.Trim()));
        }

        foreach (var directory in settings.Directories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var full = Path.GetFullPath(Path.Combine(contentRoot, directory));
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*" + PluginPackage.Extension).Order(StringComparer.Ordinal))
            {
                try
                {
                    var package = PluginPackage.Verify(file, trusted);
                    entries.Add(new PluginEntry
                    {
                        Manifest = package.Manifest,
                        Source = file,
                        SignerKeyId = package.Signer.KeyId,
                        PackageHash = package.PackageHash,
                        Package = package,
                    });
                }
                catch (Exception ex) when (ex is PluginPackageException or IOException or UnauthorizedAccessException)
                {
                    rejected.Add(new RejectedPackage(file, ex.Message));
                }
            }
        }

        // One package per ID: the first found wins (built-in, then by directory and file name).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Id))
            {
                entry.Fail($"Another package for {entry.Id} was found first; only one is loaded.");
            }
            else if (disabled.Contains(entry.Id))
            {
                entry.Status = PluginStatus.Disabled;
            }
            else if (!SdkVersions.TryParse(entry.Manifest.SdkVersion, out var wanted) || !SdkVersions.IsCompatible(wanted, serverSdk))
            {
                entry.Fail($"It needs plugin SDK {entry.Manifest.SdkVersion}; this server has {PluginSdk.Version}.");
            }
        }

        var cache = settings.CacheDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(Path.Combine(contentRoot, configured))
            : Path.Combine(Path.GetTempPath(), "nkeeper-plugins");
        foreach (var entry in DependencyOrder(entries))
        {
            if (entry.Status != PluginStatus.Loaded || entry.Instance is not null)
            {
                continue;
            }

            var dependencies = new List<PluginEntry>();
            foreach (var id in entry.Manifest.Dependencies)
            {
                var dependency = entries.FirstOrDefault(e => e.Id == id && e.Status == PluginStatus.Loaded && e.Error is null);
                if (dependency is null)
                {
                    entry.Fail($"It needs the {id} plugin, which isn't loaded.");
                    break;
                }

                dependencies.Add(dependency);
            }

            if (entry.Status == PluginStatus.Loaded)
            {
                TryLoad(entry, dependencies, cache, configuration);
            }
        }

        return new PluginCatalog(entries, rejected, [.. trusted.Select(k => k.KeyId)]);
    }

    private static PluginEntry BuiltIn(string assemblyName)
    {
        var placeholder = new PluginManifest { Id = assemblyName.ToLowerInvariant(), Name = assemblyName, Version = "0.0.0", SdkVersion = PluginSdk.Version };
        try
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            var type = PluginType(assembly);
            var info = type.GetCustomAttribute<NKeeperPluginAttribute>()
                ?? throw new InvalidOperationException($"{type.FullName} has no [NKeeperPlugin] attribute with its ID.");
            return new PluginEntry
            {
                Manifest = placeholder with
                {
                    Id = info.Id,
                    Name = info.Name,
                    Version = assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    Dependencies = info.Dependencies,
                    Backend = new PluginBackend { Assembly = $"backend/{assemblyName}.dll" },
                },
                Assembly = assembly,
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or InvalidOperationException)
        {
            var entry = new PluginEntry { Manifest = placeholder };
            entry.Fail(ex.Message);
            return entry;
        }
    }

    private static void TryLoad(PluginEntry entry, List<PluginEntry> dependencies, string cache, IConfiguration configuration)
    {
        try
        {
            if (entry.Source is not null)
            {
                var directory = Path.Combine(cache, $"{entry.Id}-{entry.PackageHash![..16]}");
                if (!PluginPackage.MatchesManifest(directory, entry.Manifest))
                {
                    // Extracting checks every file against the signed manifest again.
                    entry.Package!.ExtractTo(directory);
                }

                entry.Directory = directory;
                if (entry.Manifest.Backend is { } backend)
                {
                    var context = new PluginLoadContext(entry.Id, Path.Combine(directory, backend.Assembly), [.. dependencies.Select(d => d.LoadContext).OfType<PluginLoadContext>()]);
                    entry.LoadContext = context;
                    entry.Assembly = context.LoadMain();
                }
            }

            if (entry.Assembly is not null)
            {
                entry.Instance = (INKeeperPlugin)Activator.CreateInstance(PluginType(entry.Assembly))!;
            }

            entry.Context = new PluginContext(
                entry.Id, entry.Manifest.Name, entry.Manifest.Version, entry.Directory,
                configuration.GetSection($"{PluginsSettings.SectionName}:Settings:{entry.Id}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Fail(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message);
        }
    }

    private static Type PluginType(Assembly assembly)
    {
        var types = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INKeeperPlugin).IsAssignableFrom(t))
            .ToList();
        return types.Count == 1
            ? types[0]
            : throw new InvalidOperationException($"{assembly.GetName().Name} must have exactly one public class implementing INKeeperPlugin (it has {types.Count}).");
    }

    /// <summary>Plugins after the ones they depend on; a cycle fails every plugin in it.</summary>
    private static List<PluginEntry> DependencyOrder(List<PluginEntry> entries)
    {
        var byId = entries.Where(e => e.Status == PluginStatus.Loaded).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var ordered = new List<PluginEntry>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in byId.Values)
        {
            Visit(entry);
        }

        return [.. ordered, .. entries.Except(ordered)];

        void Visit(PluginEntry entry)
        {
            if (state.TryGetValue(entry.Id, out var done))
            {
                if (!done)
                {
                    entry.Fail("Its dependencies form a cycle.");
                }

                return;
            }

            state[entry.Id] = false;
            foreach (var id in entry.Manifest.Dependencies)
            {
                if (byId.TryGetValue(id, out var dependency))
                {
                    Visit(dependency);
                }
            }

            state[entry.Id] = true;
            ordered.Add(entry);
        }
    }

    private static List<PluginPublicKey> TrustedKeys(PluginsSettings settings, string contentRoot, out List<RejectedPackage> problems)
    {
        problems = [];
        var keys = new List<PluginPublicKey>();
        foreach (var text in settings.TrustedKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            if (PluginPublicKey.TryParse(text, out var key))
            {
                keys.Add(key);
            }
            else
            {
                problems.Add(new RejectedPackage($"{PluginsSettings.SectionName}:TrustedKeys", $"“{text}” isn't an Ed25519 public key in base64."));
            }
        }

        var directory = Path.Combine(contentRoot, "trusted-keys");
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.pub"))
            {
                if (PluginPublicKey.TryParse(File.ReadAllText(file), out var key))
                {
                    keys.Add(key);
                }
                else
                {
                    problems.Add(new RejectedPackage(file, "This file isn't an Ed25519 public key in base64."));
                }
            }
        }

        return [.. keys.Distinct()];
    }
}
