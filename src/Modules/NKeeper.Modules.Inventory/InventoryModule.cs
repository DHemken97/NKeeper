using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Modules;

namespace NKeeper.Modules.Inventory;

/// <summary>Datacenters, nodes and their connection profiles: the connection tree.</summary>
public sealed class InventoryModule : INKeeperModule
{
    public string Id => "inventory";

    public string DisplayName => "Inventory";

    public string Description => "Datacenters, nodes and connection profiles.";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Inventory uses the shared AppDbContext and needs no extra services yet.
    }

    public void MapEndpoints(IEndpointRouteBuilder group) => group.MapInventoryEndpoints();
}
