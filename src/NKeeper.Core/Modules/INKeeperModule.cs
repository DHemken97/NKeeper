using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NKeeper.Core.Modules;

/// <summary>
/// A feature plug-in (inventory, SSH terminal, RDP gateway, DNS, patching, ...).
/// The host discovers modules at startup, lets each register services, and mounts
/// its endpoints under <c>/api/{Id}</c> behind authentication.
///
/// A module can be switched off with <c>Modules:{Id}:Enabled = false</c> in configuration.
/// </summary>
public interface INKeeperModule
{
    /// <summary>Short, URL-safe, lowercase identifier. Also used as the route prefix and the client module id.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <param name="services">Application service collection.</param>
    /// <param name="configuration">The module's own section (<c>Modules:{Id}</c>).</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <param name="group">
    /// A route group at <c>/api/{Id}</c> that already requires an authenticated session.
    /// SignalR hubs can be mapped here too.
    /// </param>
    void MapEndpoints(IEndpointRouteBuilder group);
}

/// <summary>Public description of a loaded module, served to the client at <c>GET /api/modules</c>.</summary>
public sealed record ModuleDescriptor(string Id, string DisplayName, string Description);
