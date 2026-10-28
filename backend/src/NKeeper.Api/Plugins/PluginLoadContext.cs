using System.Reflection;
using System.Runtime.Loader;

namespace NKeeper.Api.Plugins;

/// <summary>
/// Loads one plugin's assemblies from its directory. Anything the server itself ships (the SDK,
/// ASP.NET Core, EF Core, SSH.NET…) comes from the server, so plugins and server share those types;
/// then the plugins it depends on; then its own files.
/// </summary>
internal sealed class PluginLoadContext(string pluginId, string mainAssemblyPath, IReadOnlyList<PluginLoadContext> dependencies)
    : AssemblyLoadContext($"plugin:{pluginId}")
{
    private static readonly Lazy<HashSet<string>> ServerAssemblies = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    public Assembly LoadMain() => LoadFromAssemblyPath(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && ServerAssemblies.Value.Contains(name))
        {
            return null;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.TryLoadOwn(assemblyName) is { } shared)
            {
                return shared;
            }
        }

        return TryLoadOwn(assemblyName);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    private Assembly? TryLoadOwn(AssemblyName assemblyName)
    {
        if (Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(assemblyName, a.GetName())) is { } loaded)
        {
            return loaded;
        }

        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
    }
}
