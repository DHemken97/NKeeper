using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Plugins;

namespace NKeeper.Api.Plugins;

internal static class PluginRegistration
{
    /// <summary>
    /// Loads the plugins and lets each register its services, with its own settings and per-host
    /// values (keyed by its ID). Runs before the app is built.
    /// </summary>
    public static PluginCatalog AddNKeeperPlugins(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var catalog = PluginLoader.Load(configuration, contentRoot);
        services.AddSingleton(catalog);
        services.AddSingleton<HostFieldRegistry>();
        services.AddScoped<IHostDirectory>(sp => sp.GetRequiredService<Inventory.HostManager>());
        services.AddHostedService<PluginStartupReport>();
        foreach (var plugin in catalog.Loaded.ToList())
        {
            var id = plugin.Id;
            services.AddKeyedScoped<IPluginSettings>(id, (sp, _) => ActivatorUtilities.CreateInstance<PluginSettingsStore>(sp, id));
            services.AddKeyedScoped<IHostPluginData>(id, (sp, _) => ActivatorUtilities.CreateInstance<HostPluginDataStore>(sp, id));
            try
            {
                plugin.Instance?.ConfigureServices(services, plugin.Context!);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                plugin.Fail($"Setting it up failed: {ex.Message}");
            }
        }

        return catalog;
    }

    /// <summary>Adds the loaded plugins' controllers, with their routes under <c>/api/plugins/{id}/</c>.</summary>
    public static IMvcBuilder AddPluginControllers(this IMvcBuilder mvc, PluginCatalog catalog)
    {
        foreach (var plugin in catalog.Loaded)
        {
            if (plugin.Assembly is { } assembly && !mvc.PartManager.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == assembly))
            {
                mvc.PartManager.ApplicationParts.Add(new AssemblyPart(assembly));
            }
        }

        mvc.Services.Configure<MvcOptions>(options => options.Conventions.Add(new PluginRouteConvention(catalog)));
        return mvc;
    }
}

/// <summary>
/// Puts every plugin controller's routes under <c>/api/plugins/{id}/</c>, so plugins can't take over
/// each other's or NKeeper's own endpoints. An absolute route (starting with <c>/</c> or <c>~/</c>) is refused.
/// </summary>
internal sealed class PluginRouteConvention(PluginCatalog catalog) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            if (catalog.ForAssembly(controller.ControllerType.Assembly) is not { } plugin)
            {
                continue;
            }

            var absolute = controller.Selectors.Concat(controller.Actions.SelectMany(a => a.Selectors))
                .Select(s => s.AttributeRouteModel?.Template)
                .FirstOrDefault(t => t is not null && (t.StartsWith('/') || t.StartsWith("~/", StringComparison.Ordinal)));
            if (absolute is not null)
            {
                throw new InvalidOperationException(
                    $"The {plugin.Id} plugin's {controller.ControllerName} controller has an absolute route ({absolute}); plugin routes are relative to /{PluginSdk.RouteFor(plugin.Id)}/.");
            }

            var prefix = new AttributeRouteModel(new RouteAttribute(PluginSdk.RouteFor(plugin.Id)));
            foreach (var selector in controller.Selectors)
            {
                selector.AttributeRouteModel = selector.AttributeRouteModel is null
                    ? prefix
                    : AttributeRouteModel.CombineAttributeRouteModel(prefix, selector.AttributeRouteModel);
            }
        }
    }
}

/// <summary>Logs, at start-up, which plugins were loaded and why the others weren't.</summary>
internal sealed class PluginStartupReport(PluginCatalog catalog, ILogger<PluginStartupReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var plugin in catalog.Plugins)
        {
            switch (plugin.Status)
            {
                case PluginStatus.Loaded:
                    logger.LogInformation("Plugin {Id} {Version} loaded ({Source}).", plugin.Id, plugin.Manifest.Version, plugin.SignerKeyId is { } key ? $"signed with key {key}" : "built in");
                    break;
                case PluginStatus.Disabled:
                    logger.LogInformation("Plugin {Id} is turned off (Plugins:Disabled).", plugin.Id);
                    break;
                default:
                    logger.LogError("Plugin {Id} wasn't loaded: {Error}", plugin.Id, plugin.Error);
                    break;
            }
        }

        foreach (var rejected in catalog.Rejected)
        {
            logger.LogError("Plugin package {Source} was refused: {Error}", rejected.Source, rejected.Error);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
