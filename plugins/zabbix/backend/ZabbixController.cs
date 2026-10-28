using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Sdk.Access;
using NKeeper.Sdk.Auditing;
using NKeeper.Sdk.Inventory;
using NKeeper.Sdk.Plugins;
using NKeeper.Sdk.Security;
using NKeeper.Sdk.Sessions;
using NKeeper.Sdk.Web;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// Zabbix (under /api/plugins/zabbix/): its settings (administrators), what it knows about a linked
/// host (anyone who can see the host), searching it for a host to link, and comparing it with
/// NKeeper's hosts to link them or add the ones NKeeper doesn't have. NKeeper only reads from Zabbix.
/// </summary>
[ApiController]
[Authorize]
[BlockedWhileInstanceLocked]
public sealed class ZabbixController(
    NKeeperDbContext db,
    ZabbixConnection zabbix,
    HostAccess access,
    IHostDirectory hosts,
    [FromKeyedServices(ZabbixPlugin.Id)] IPluginSettings settings,
    [FromKeyedServices(ZabbixPlugin.Id)] IHostPluginData links) : ControllerBase
{
    private const int SearchLimit = 50;

    [HttpGet("status")]
    [ProducesResponseType<ZabbixStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ZabbixStatusResponse> Status(CancellationToken cancellationToken) =>
        await zabbix.GetFrontendAsync(cancellationToken) is { } frontend
            ? new ZabbixStatusResponse(true, frontend.ToString())
            : new ZabbixStatusResponse(false, null);

    [HttpGet("settings")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ZabbixSettingsResponse> GetSettings(CancellationToken cancellationToken) =>
        ToResponse(await zabbix.GetSettingsAsync(cancellationToken));

    [Audit("settings.zabbix", AuditTarget.None)]
    [HttpPut("settings")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ZabbixSettingsResponse> UpdateSettings(ZabbixSettingsRequest request, CancellationToken cancellationToken)
    {
        var saved = await zabbix.GetSettingsAsync(cancellationToken);
        if (request.Enabled && string.IsNullOrEmpty(request.ApiToken) && !saved.HasApiToken)
        {
            throw new InventoryValidationException("apiToken", "Enter an API token.");
        }

        var url = ZabbixUrl.TryNormalize(request.Url, out var frontend) ? frontend.ToString() : string.Empty;
        await settings.SaveAsync(
            new Dictionary<string, string?>
            {
                [ZabbixSettings.EnabledKey] = request.Enabled ? "true" : "false",
                [ZabbixSettings.UrlKey] = url,
                [ZabbixSettings.FingerprintKey] = TrustedFingerprint(saved, url, request.TrustFingerprint),
            },
            string.IsNullOrEmpty(request.ApiToken) ? null : new Dictionary<string, string?> { [ZabbixSettings.ApiTokenKey] = request.ApiToken.Trim() },
            cancellationToken);
        return ToResponse(await zabbix.GetSettingsAsync(cancellationToken));
    }

    /// <summary>
    /// Tests the settings in the form (not necessarily saved; an empty API token means the saved
    /// one): the server's certificate, its API version, and the token. A certificate that isn't
    /// publicly valid must be trusted first: the answer is then "trustRequired" with the certificate.
    /// </summary>
    [HttpPost("settings/test")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixTestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ZabbixTestResponse>> Test(ZabbixSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!ZabbixUrl.TryNormalize(request.Url, out var frontend))
        {
            throw new InventoryValidationException("url", "Enter the Zabbix address, e.g. https://zabbix.example.com/zabbix.");
        }

        var saved = await zabbix.GetSettingsAsync(cancellationToken);
        var token = !string.IsNullOrEmpty(request.ApiToken) ? request.ApiToken.Trim()
            : await zabbix.GetApiTokenAsync(cancellationToken)
            ?? throw new InventoryValidationException("apiToken", "Enter an API token.");

        try
        {
            string? pinned = null;
            if (frontend.Scheme == Uri.UriSchemeHttps)
            {
                var certificate = await WebTargetProbe.ProbeAsync(frontend.Host, frontend.Port, cancellationToken);
                if (!certificate.IsValid)
                {
                    var trusted = TrustedFingerprint(saved, frontend.ToString(), request.TrustFingerprint);
                    if (!string.Equals(trusted, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ZabbixTestResponse(
                            "trustRequired", null, null,
                            new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
                    }

                    pinned = certificate.Fingerprint;
                }
            }

            using var client = new ZabbixClient(ZabbixUrl.ApiEndpoint(frontend), token, pinned);
            var version = await client.GetVersionAsync(cancellationToken);
            var count = await ZabbixQueries.CountHostsAsync(client, cancellationToken);
            return new ZabbixTestResponse("ready", version, count, null);
        }
        catch (SessionException ex)
        {
            throw new InventoryValidationException(ex.Code == SessionErrorCodes.AuthenticationFailed ? "apiToken" : "url", ex.Message);
        }
    }

    /// <summary>What Zabbix knows about a host: its state, interfaces, inventory, latest values and problems.</summary>
    [HttpGet("hosts/{id:guid}")]
    [ProducesResponseType<ZabbixHostOverview>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ZabbixHostOverview>> Overview(Guid id, CancellationToken cancellationToken)
    {
        if (!await access.Visible(RequestUser.From(User)).AnyAsync(h => h.Id == id, cancellationToken))
        {
            return Failure(new SessionException(SessionErrorCodes.HostNotFound, "Host not found."));
        }

        if (await links.GetAsync(id, ZabbixPlugin.HostIdField, cancellationToken) is not { } zabbixHostId)
        {
            return Failure(new SessionException(ZabbixErrorCodes.NotLinked, "This host isn't linked to a Zabbix host."));
        }

        return await WithZabbixAsync(client => ZabbixQueries.GetOverviewAsync(client, zabbixHostId, cancellationToken), cancellationToken);
    }

    /// <summary>Zabbix hosts whose name, technical name, IP address or DNS name contains the text, to link one. For users who can manage hosts.</summary>
    [HttpGet("hosts")]
    [ProducesResponseType<List<ZabbixHostSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<ZabbixHostSummary>>> Search([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var user = RequestUser.From(User);
        if (await access.ManageableAsync(user, cancellationToken) is { Count: 0 })
        {
            return Forbid();
        }

        var text = search?.Trim() ?? string.Empty;
        if (text.Length is 0 or > 255)
        {
            return new List<ZabbixHostSummary>();
        }

        return await WithZabbixAsync(client => ZabbixQueries.SearchAsync(client, text, SearchLimit, cancellationToken), cancellationToken);
    }

    /// <summary>Zabbix's hosts next to NKeeper's: linked, probably the same machine, and only on one side. Administrators only.</summary>
    [HttpGet("comparison")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixComparisonResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ZabbixComparisonResponse>> Comparison(CancellationToken cancellationToken)
    {
        var linked = await links.GetAllAsync(ZabbixPlugin.HostIdField, cancellationToken);
        var candidates = (await db.Hosts.AsNoTracking()
                .OrderBy(h => h.Name)
                .Select(h => new ComparedHost(h.Id, h.Name, h.Hostname, h.IpAddress, h.MacAddress, true))
                .ToListAsync(cancellationToken))
            .Select(h => new ZabbixMatcher.Candidate(h, linked.GetValueOrDefault(h.Id)))
            .ToList();
        return await WithZabbixAsync(async client =>
        {
            var zabbixHosts = await ZabbixQueries.ListHostsAsync(client, cancellationToken);
            return ZabbixMatcher.Compare(candidates, [.. zabbixHosts.Select(r => r.Summary)]);
        }, cancellationToken);
    }

    /// <summary>Links hosts to Zabbix hosts, or unlinks them. For hosts the user can manage.</summary>
    [Audit("host.link_zabbix", AuditTarget.None)]
    [HttpPost("links")]
    [ProducesResponseType<List<HostResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IReadOnlyList<HostResponse>> Link(ZabbixLinksRequest request, CancellationToken cancellationToken) =>
        await hosts.SetPluginValuesAsync(
            ZabbixPlugin.Id,
            ZabbixPlugin.HostIdField,
            request.Links.GroupBy(l => l.HostId).ToDictionary(g => g.Key, g => g.Last().ZabbixHostId),
            RequestUser.From(User),
            cancellationToken);

    /// <summary>
    /// Adds hosts for Zabbix hosts NKeeper doesn't have, from what Zabbix knows: the visible name,
    /// the main interface's IP address and DNS name, the inventory's MAC address and serial number,
    /// the description and tags (and, if asked, the host groups as <c>group</c> tags), each linked to
    /// its Zabbix host. Zabbix hosts already linked, or without an address, are skipped. Administrators only.
    /// </summary>
    [Audit("host.create_from_zabbix", AuditTarget.None)]
    [HttpPost("import")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ZabbixImportResponse>> Import(ZabbixImportRequest request, CancellationToken cancellationToken)
    {
        var ids = request.ZabbixHostIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();
        if (ids.Any(i => i.Length > ZabbixSettings.HostIdMaxLength || !i.All(char.IsAsciiDigit)))
        {
            throw new InventoryValidationException("zabbixHostIds", "A Zabbix host ID is a number.");
        }

        return await WithZabbixAsync(async client =>
        {
            var records = await ZabbixQueries.ListHostsAsync(client, cancellationToken, ids);
            var linked = await links.GetAllAsync(ZabbixPlugin.HostIdField, cancellationToken);
            var linkedHostIds = linked.Where(l => ids.Contains(l.Value)).Select(l => l.Key).ToList();
            var names = await db.Hosts.AsNoTracking().Where(h => linkedHostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
            var linkedTo = linked.Where(l => names.ContainsKey(l.Key)).GroupBy(l => l.Value).ToDictionary(g => g.Key, g => names[g.First().Key]);

            var skipped = new List<ZabbixImportSkipped>();
            var create = new List<NewHost>();
            foreach (var record in records)
            {
                var summary = record.Summary;
                if (linkedTo.TryGetValue(summary.HostId, out var nkeeperName))
                {
                    skipped.Add(new ZabbixImportSkipped(summary.HostId, summary.Name, $"Already in NKeeper as “{nkeeperName}”."));
                    continue;
                }

                var ip = summary.IpAddresses.Count > 0 ? summary.IpAddresses[0] : null;
                var hostname = summary.DnsNames.FirstOrDefault(NetworkAddress.IsValidHostname)
                    // The technical name is often the machine's hostname.
                    ?? (NetworkAddress.IsValidHostname(summary.Host) && ip is null ? summary.Host : null);
                if (hostname is null && ip is null)
                {
                    skipped.Add(new ZabbixImportSkipped(summary.HostId, summary.Name, "Zabbix has no IP address or DNS name for it."));
                    continue;
                }

                create.Add(new NewHost(summary.Name)
                {
                    Hostname = hostname,
                    IpAddress = ip,
                    MacAddress = summary.MacAddresses.Count > 0 ? summary.MacAddresses[0] : null,
                    Description = record.Description,
                    SerialNumber = record.SerialNumber,
                    Tags = [.. summary.Tags.Select(t => (t.Name, t.Value)), .. request.GroupsAsTags ? record.Groups.Select(g => ("group", (string?)g)) : []],
                    Plugins = new Dictionary<string, IReadOnlyDictionary<string, string>>
                    {
                        [ZabbixPlugin.Id] = new Dictionary<string, string> { [ZabbixPlugin.HostIdField] = summary.HostId },
                    },
                });
            }

            var created = create.Count == 0 ? [] : await hosts.CreateAsync(create, RequestUser.From(User), cancellationToken);
            var missing = ids.Except(records.Select(r => r.Summary.HostId)).Select(i => new ZabbixImportSkipped(i, i, "Zabbix has no host with this ID, or the API token can't see it."));
            return new ZabbixImportResponse(created, [.. skipped, .. missing]);
        }, cancellationToken);
    }

    /// <summary>The trusted certificate to keep: the newly trusted one, else the saved one while the server's address is the same.</summary>
    private static string? TrustedFingerprint(ZabbixSettings saved, string url, string? trustFingerprint)
    {
        if (!string.IsNullOrWhiteSpace(trustFingerprint))
        {
            return trustFingerprint.Trim();
        }

        return Uri.TryCreate(saved.Url, UriKind.Absolute, out var before) && Uri.TryCreate(url, UriKind.Absolute, out var after)
            && string.Equals(before.Host, after.Host, StringComparison.OrdinalIgnoreCase) && before.Port == after.Port
            ? saved.TrustedCertificateFingerprint
            : null;
    }

    private async Task<ActionResult<T>> WithZabbixAsync<T>(Func<ZabbixClient, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await zabbix.OpenAsync(cancellationToken);
            return await action(client);
        }
        catch (SessionException ex)
        {
            return Failure(ex);
        }
    }

    /// <summary>Problem details with the code; Zabbix not set up is 409, a missing or unlinked host 404, the rest as sessions answer.</summary>
    private ObjectResult Failure(SessionException ex)
    {
        var status = ex.Code switch
        {
            ZabbixErrorCodes.Disabled => StatusCodes.Status409Conflict,
            ZabbixErrorCodes.NotLinked or ZabbixErrorCodes.HostNotFound => StatusCodes.Status404NotFound,
            _ => (int?)null,
        };
        if (status is null)
        {
            return this.SessionFailure(ex);
        }

        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: ex.Message);
        problem.Extensions["code"] = ex.Code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private static ZabbixSettingsResponse ToResponse(ZabbixSettings settings) =>
        new(settings.Enabled, settings.Url, settings.HasApiToken, settings.TrustedCertificateFingerprint, settings.UpdatedAtUtc);
}
