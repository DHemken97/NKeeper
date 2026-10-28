using System.Runtime.Loader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Infrastructure.Identity;
using NKeeper.Sdk.Access;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Plugins;

namespace NKeeper.SamplePlugin;

[NKeeperPlugin("sample", "Sample")]
public sealed class SamplePlugin : INKeeperPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        context.AddHostField(new HostField("ticket", 20, "[A-Z]+-[0-9]+", "A ticket is like ABC-123."));
        services.AddSingleton(new Greeting(context.Configuration["Greeting"] ?? "Hello"));
    }
}

public sealed record Greeting(string Text);

public sealed record SampleSettingsRequest(string? Colour, string? Token);

[ApiController]
public sealed class SampleController(
    Greeting greeting,
    [FromKeyedServices("sample")] IPluginSettings settings,
    [FromKeyedServices("sample")] IHostPluginData hostData,
    IHostDirectory hosts) : ControllerBase
{
    [HttpGet("hello")]
    public object Hello() => new
    {
        message = greeting.Text,
        loadContext = AssemblyLoadContext.GetLoadContext(typeof(SampleController).Assembly)?.Name,
    };

    [HttpGet("settings")]
    [Authorize(Roles = SystemRoles.Administrator)]
    public async Task<object> GetSettings(CancellationToken cancellationToken)
    {
        var saved = await settings.GetAsync(cancellationToken);
        return new
        {
            colour = saved.Get("colour"),
            hasToken = saved.HasSecret("token"),
            token = await settings.GetSecretAsync("token", cancellationToken),
        };
    }

    [HttpPut("settings")]
    [Authorize(Roles = SystemRoles.Administrator)]
    public async Task<IActionResult> SaveSettings(SampleSettingsRequest request, CancellationToken cancellationToken)
    {
        await settings.SaveAsync(
            new Dictionary<string, string?> { ["colour"] = request.Colour },
            request.Token is null ? null : new Dictionary<string, string?> { ["token"] = request.Token },
            cancellationToken);
        return NoContent();
    }

    [HttpGet("tickets")]
    public async Task<IReadOnlyDictionary<Guid, string>> Tickets(CancellationToken cancellationToken) =>
        await hostData.GetAllAsync("ticket", cancellationToken);

    [HttpPost("tickets/{id:guid}")]
    public async Task<IReadOnlyList<HostResponse>> SetTicket(Guid id, [FromQuery] string? ticket, CancellationToken cancellationToken) =>
        await hosts.SetPluginValuesAsync("sample", "ticket", new Dictionary<Guid, string?> { [id] = ticket }, RequestUser.From(User), cancellationToken);
}
