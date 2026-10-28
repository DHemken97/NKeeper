using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NKeeper.Sdk.Plugins;

/// <summary>The plugin SDK this server implements; a package's <c>sdkVersion</c> must have the same major and no newer minor.</summary>
public static class PluginSdk
{
    public const string Version = "1.0";

    /// <summary>Plugins' HTTP APIs live under this, followed by the plugin's ID: <c>/api/plugins/zabbix/…</c>.</summary>
    public const string RoutePrefix = "api/plugins";

    public static string RouteFor(string pluginId) => $"{RoutePrefix}/{pluginId}";
}

/// <summary>
/// A plugin's server part. The server creates one instance per plugin (with a parameterless
/// constructor) before it starts, from the one public non-abstract class implementing this in the
/// plugin's assembly.
/// </summary>
/// <remarks>
/// A plugin's HTTP API is MVC controllers in its assembly, found automatically. Their attribute
/// routes are relative to <c>/api/plugins/{id}/</c> (<c>[HttpGet("status")]</c> answers
/// <c>/api/plugins/{id}/status</c>; <c>ui</c> is reserved for the plugin's browser files). They get
/// what every NKeeper endpoint gets: a signed-in user unless they say otherwise, antiforgery tokens
/// on changes, account checks, problem details for <c>InventoryValidationException</c> and
/// <c>HostAccessDeniedException</c>, and refusal while the instance is locked when marked
/// <c>[BlockedWhileInstanceLocked]</c>. A plugin is trusted code: it runs in the server's process
/// with the server's rights, which is why packages must be signed by a key the server trusts.
/// </remarks>
public interface INKeeperPlugin
{
    /// <summary>Registers the plugin's services, settings and per-host fields.</summary>
    void ConfigureServices(IServiceCollection services, PluginContext context);
}

/// <summary>
/// The plugin's ID, name and dependencies, on its <see cref="INKeeperPlugin"/> class. A package's
/// manifest says the same; this is what the server goes by when the plugin is loaded without a
/// package (from the server's own files, in development and tests).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NKeeperPluginAttribute(string id, string name) : Attribute
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string[] Dependencies { get; init; } = [];
}

/// <summary>What a plugin knows about itself while it's set up.</summary>
public sealed class PluginContext(string id, string name, string version, string? directory, IConfiguration configuration)
{
    private readonly List<HostField> _hostFields = [];

    /// <summary>The plugin's ID from its manifest, e.g. <c>zabbix</c>.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Version { get; } = version;

    /// <summary>Where the plugin's files were extracted (its backend/ and frontend/), or null when it's loaded from the server's own assemblies (development and tests).</summary>
    public string? Directory { get; } = directory;

    /// <summary>The <c>Plugins:Settings:{id}</c> configuration section: settings an administrator sets in appsettings or the environment rather than the web app.</summary>
    public IConfiguration Configuration { get; } = configuration;

    public IReadOnlyList<HostField> HostFields => _hostFields;

    /// <summary>
    /// Declares a value this plugin keeps on hosts (e.g. the host's ID in another system). It's
    /// returned with every host under <c>plugins.{id}.{name}</c>, can be set in the host editor, is
    /// copied when an instance moves to a new server, and is deleted with the host.
    /// </summary>
    public void AddHostField(HostField field)
    {
        if (_hostFields.Any(f => f.Name == field.Name))
        {
            throw new InvalidOperationException($"The host field {field.Name} is declared twice.");
        }

        _hostFields.Add(field);
    }
}

/// <summary>
/// A per-host value a plugin declares. Values are trimmed; an empty one removes it.
/// </summary>
/// <param name="Name">camelCase, e.g. <c>hostId</c>.</param>
/// <param name="MaxLength">At most 512.</param>
/// <param name="Pattern">A regular expression the whole value must match, or null for any text.</param>
/// <param name="Message">What's shown when the value is too long or doesn't match, e.g. "A Zabbix host ID is a number, e.g. 10084."</param>
public sealed record HostField(string Name, int MaxLength, string? Pattern, string Message)
{
    public const int MaxValueLength = 512;
}
