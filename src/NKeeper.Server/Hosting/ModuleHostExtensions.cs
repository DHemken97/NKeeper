using NKeeper.Core.Modules;

namespace NKeeper.Server.Hosting;

/// <summary>The modules enabled for this run, in registration order.</summary>
public sealed class ModuleCatalog(IReadOnlyList<INKeeperModule> modules)
{
    public IReadOnlyList<INKeeperModule> Modules { get; } = modules;

    public IEnumerable<ModuleDescriptor> Describe() =>
        Modules.Select(m => new ModuleDescriptor(m.Id, m.DisplayName, m.Description));
}

public static class ModuleHostExtensions
{
    /// <summary>
    /// Registers feature modules. Modules switched off with <c>Modules:{Id}:Enabled = false</c> are skipped entirely.
    /// </summary>
    public static WebApplicationBuilder AddNKeeperModules(this WebApplicationBuilder builder, params INKeeperModule[] modules)
    {
        var duplicate = modules.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Module id '{duplicate.Key}' is registered more than once.");
        }

        var enabled = new List<INKeeperModule>();
        foreach (var module in modules)
        {
            var section = builder.Configuration.GetSection($"Modules:{module.Id}");
            if (!section.GetValue("Enabled", true))
            {
                continue;
            }

            module.ConfigureServices(builder.Services, section);
            enabled.Add(module);
        }

        builder.Services.AddSingleton(new ModuleCatalog(enabled));
        return builder;
    }

    /// <summary>Mounts each enabled module at <c>/api/{Id}</c> behind authentication, plus <c>GET /api/modules</c>.</summary>
    public static WebApplication MapNKeeperModules(this WebApplication app)
    {
        var catalog = app.Services.GetRequiredService<ModuleCatalog>();

        app.MapGet("/api/modules", (ModuleCatalog modules) => modules.Describe())
            .RequireAuthorization()
            .WithTags("Modules");

        foreach (var module in catalog.Modules)
        {
            var group = app.MapGroup($"/api/{module.Id}")
                .RequireAuthorization()
                .WithTags(module.DisplayName);

            module.MapEndpoints(group);
        }

        return app;
    }
}
