using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using NKeeper.Api.Plugins;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Controllers;

/// <summary>A loaded plugin's browser part: the script and styles the web app loads after sign-in.</summary>
public sealed record PluginUiResponse(string Id, string Name, string Version, string? Script, IReadOnlyList<string> Styles);

/// <summary>A plugin package the server found, for administrators.</summary>
/// <param name="Status">loaded, disabled or failed.</param>
/// <param name="Signer">The key ID the package is signed with; null for a plugin built into the server.</param>
public sealed record PluginInfoResponse(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    string Status,
    string? Error,
    string? Signer,
    string? Source,
    IReadOnlyList<string> Dependencies,
    bool HasBackend,
    bool HasFrontend);

/// <summary>A package that was refused before its manifest could be trusted.</summary>
public sealed record RejectedPluginResponse(string Source, string Error);

public sealed record PluginsOverviewResponse(
    string SdkVersion,
    IReadOnlyList<PluginInfoResponse> Plugins,
    IReadOnlyList<RejectedPluginResponse> Rejected,
    IReadOnlyCollection<string> TrustedKeys);

/// <summary>
/// The installed plugins: what the web app loads (everyone signed in), their files, and the full
/// picture for administrators, with the reason any plugin wasn't loaded.
/// </summary>
[ApiController]
[Authorize]
public sealed class PluginsController(PluginCatalog catalog) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    [HttpGet("api/plugins")]
    [ProducesResponseType<List<PluginUiResponse>>(StatusCodes.Status200OK)]
    public List<PluginUiResponse> List() =>
        [.. catalog.Loaded
            .Where(p => p.Manifest.Frontend is not null && p.Directory is not null)
            .Select(p => new PluginUiResponse(
                p.Id,
                p.Manifest.Name,
                p.Manifest.Version,
                AssetUrl(p, p.Manifest.Frontend!.Entry),
                [.. p.Manifest.Frontend.Styles.Select(s => AssetUrl(p, s))]))];

    /// <summary>A file of a plugin's frontend, exactly as signed in its package.</summary>
    [HttpGet("api/plugins/{id}/ui/{**path}")]
    public IActionResult Asset(string id, string path)
    {
        var plugin = catalog.Find(id);
        var file = $"frontend/{path}";
        if (plugin?.Directory is not { } directory || !plugin.Manifest.Files.TryGetValue(file, out var hash))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(file, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        // Revalidated every time, so an upgraded plugin's files are picked up at once.
        Response.Headers.CacheControl = "no-cache";
        return PhysicalFile(Path.Combine(directory, file), contentType, fileDownloadName: null, lastModified: null, entityTag: new EntityTagHeaderValue($"\"{hash}\""), enableRangeProcessing: false);
    }

    [HttpGet("api/admin/plugins")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<PluginsOverviewResponse>(StatusCodes.Status200OK)]
    public PluginsOverviewResponse Overview() => new(
        NKeeper.Sdk.Plugins.PluginSdk.Version,
        [.. catalog.Plugins.Select(p => new PluginInfoResponse(
            p.Id,
            p.Manifest.Name,
            p.Manifest.Version,
            p.Manifest.Description,
            p.Manifest.Author,
            p.Status.ToString().ToLowerInvariant(),
            p.Error,
            p.SignerKeyId,
            p.Source is null ? null : Path.GetFileName(p.Source),
            p.Manifest.Dependencies,
            p.Manifest.Backend is not null,
            p.Manifest.Frontend is not null))],
        [.. catalog.Rejected.Select(r => new RejectedPluginResponse(Path.GetFileName(r.Source), r.Error))],
        catalog.TrustedKeyIds);

    /// <summary>The file's URL, versioned by the package so browsers never mix an old file with a new plugin.</summary>
    private static string AssetUrl(PluginEntry plugin, string file) =>
        $"/api/plugins/{plugin.Id}/ui/{file["frontend/".Length..]}?v={plugin.PackageHash![..12]}";
}
