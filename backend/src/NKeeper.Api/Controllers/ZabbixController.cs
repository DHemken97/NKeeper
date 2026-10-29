using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Contracts.Zabbix;
using NKeeper.Api.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Web;
using NKeeper.Api.Zabbix;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Infrastructure.Zabbix;

namespace NKeeper.Api.Controllers;

/// <summary>
/// Zabbix: its settings (administrators), what it knows about a linked host (anyone who can see the
/// host), searching it for a host to link, and comparing it with NKeeper's hosts to link them or add
/// the ones NKeeper doesn't have. NKeeper only reads from Zabbix.
/// </summary>
[ApiController]
[Authorize]
[BlockedWhileInstanceLocked]
public sealed class ZabbixController(
    NKeeperDbContext db,
    ZabbixConnection zabbix,
    HostAccess access,
    HostManager hosts,
    CredentialProtector protector,
    TimeProvider timeProvider) : ControllerBase
{
    private const int SearchLimit = 50;

    [HttpGet("api/zabbix/status")]
    [ProducesResponseType<ZabbixStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ZabbixStatusResponse> Status(CancellationToken cancellationToken) =>
        await zabbix.GetFrontendAsync(cancellationToken) is { } frontend
            ? new ZabbixStatusResponse(true, frontend.ToString())
            : new ZabbixStatusResponse(false, null);

    [HttpGet("api/admin/zabbix")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ZabbixSettingsResponse> GetSettings(CancellationToken cancellationToken) =>
        ToResponse(await zabbix.GetSettingsAsync(cancellationToken));

    [Audit("settings.zabbix", AuditTarget.None)]
    [HttpPut("api/admin/zabbix")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ZabbixSettingsResponse> UpdateSettings(ZabbixSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = await db.ZabbixSettings.FirstOrDefaultAsync(s => s.Id == ZabbixSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = new ZabbixSettings();
            db.ZabbixSettings.Add(settings);
        }

        if (request.Enabled && string.IsNullOrEmpty(request.ApiToken) && settings.ProtectedApiToken is null)
        {
            throw new InventoryValidationException("apiToken", "Enter an API token.");
        }

        var url = ZabbixUrl.TryNormalize(request.Url, out var frontend) ? frontend.ToString() : string.Empty;
        settings.TrustedCertificateFingerprint = TrustedFingerprint(settings, url, request.TrustFingerprint);
        settings.Enabled = request.Enabled;
        settings.Url = url;
        if (!string.IsNullOrEmpty(request.ApiToken))
        {
            settings.ProtectedApiToken = protector.Protect(request.ApiToken.Trim());
        }

        settings.UpdatedAtUtc = timeProvider.GetUtcNow();
        settings.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(settings);
    }

    /// <summary>
    /// Tests the settings in the form (not necessarily saved; an empty API token means the saved
    /// one): the server's certificate, its API version, and the token. A certificate that isn't
    /// publicly valid must be trusted first: the answer is then "trustRequired" with the certificate.
    /// </summary>
    [HttpPost("api/admin/zabbix/test")]
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
            : saved?.ProtectedApiToken is { } protectedToken ? protector.Unprotect(protectedToken)
            : throw new InventoryValidationException("apiToken", "Enter an API token.");

        try
        {
            string? pinned = null;
            if (frontend.Scheme == Uri.UriSchemeHttps)
            {
                var certificate = await WebTargetProbe.ProbeAsync(frontend.Host, frontend.Port, cancellationToken);
                if (!certificate.IsValid)
                {
                    var trusted = saved is null ? request.TrustFingerprint : TrustedFingerprint(saved, frontend.ToString(), request.TrustFingerprint);
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
    [HttpGet("api/hosts/{id:guid}/zabbix")]
    [ProducesResponseType<ZabbixHostOverview>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ZabbixHostOverview>> Overview(Guid id, CancellationToken cancellationToken)
    {
        var host = await access.Visible(RequestUser.From(User)).Where(h => h.Id == id)
            .Select(h => new { h.ZabbixHostId }).FirstOrDefaultAsync(cancellationToken);
        if (host is null)
        {
            return Failure(new SessionException(SessionErrorCodes.HostNotFound, "Host not found."));
        }

        if (host.ZabbixHostId is null)
        {
            return Failure(new SessionException(ZabbixErrorCodes.NotLinked, "This host isn't linked to a Zabbix host."));
        }

        return await WithZabbixAsync(client => ZabbixQueries.GetOverviewAsync(client, host.ZabbixHostId, cancellationToken), cancellationToken);
    }

    /// <summary>Zabbix hosts whose name, technical name, IP address or DNS name contains the text, to link one. For users who can manage hosts.</summary>
    [HttpGet("api/zabbix/hosts")]
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
    [HttpGet("api/zabbix/comparison")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixComparisonResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ZabbixComparisonResponse>> Comparison(CancellationToken cancellationToken)
    {
        var candidates = await db.Hosts.AsNoTracking()
            .OrderBy(h => h.Name)
            .Select(h => new ZabbixMatcher.Candidate(new ComparedHost(h.Id, h.Name, h.Hostname, h.IpAddress, h.MacAddress, true), h.ZabbixHostId))
            .ToListAsync(cancellationToken);
        return await WithZabbixAsync(async client =>
        {
            var zabbixHosts = await ZabbixQueries.ListHostsAsync(client, cancellationToken);
            return ZabbixMatcher.Compare(candidates, [.. zabbixHosts.Select(r => r.Summary)]);
        }, cancellationToken);
    }

    /// <summary>Links hosts to Zabbix hosts, or unlinks them. For hosts the user can manage.</summary>
    [Audit("host.link_zabbix", AuditTarget.None)]
    [HttpPost("api/zabbix/links")]
    [ProducesResponseType<List<HostResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public Task<List<HostResponse>> Link(ZabbixLinksRequest request, CancellationToken cancellationToken) =>
        hosts.LinkZabbixAsync(request.Links, RequestUser.From(User), cancellationToken);

    /// <summary>Adds hosts for Zabbix hosts NKeeper doesn't have, from Zabbix's data, linked to them. Administrators only.</summary>
    [Audit("host.create_from_zabbix", AuditTarget.None)]
    [HttpPost("api/zabbix/import")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [ProducesResponseType<ZabbixImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ZabbixImportResponse>> Import(ZabbixImportRequest request, CancellationToken cancellationToken)
    {
        var ids = request.ZabbixHostIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();
        if (ids.Any(i => i.Length > NKeeper.Infrastructure.Inventory.Host.ZabbixHostIdMaxLength || !i.All(char.IsAsciiDigit)))
        {
            throw new InventoryValidationException("zabbixHostIds", "A Zabbix host ID is a number.");
        }

        return await WithZabbixAsync(async client =>
        {
            var records = await ZabbixQueries.ListHostsAsync(client, cancellationToken, ids);
            var response = await hosts.CreateFromZabbixAsync(records, request.GroupsAsTags, RequestUser.From(User), cancellationToken);
            var missing = ids.Except(records.Select(r => r.Summary.HostId)).Select(i => new ZabbixImportSkipped(i, i, "Zabbix has no host with this ID, or the API token can't see it."));
            return response with { Skipped = [.. response.Skipped, .. missing] };
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

    private static ZabbixSettingsResponse ToResponse(ZabbixSettings? settings) =>
        settings is null
            ? new ZabbixSettingsResponse(false, string.Empty, false, null, null)
            : new ZabbixSettingsResponse(settings.Enabled, settings.Url, settings.ProtectedApiToken is not null, settings.TrustedCertificateFingerprint, settings.UpdatedAtUtc);
}
