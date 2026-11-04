using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Auditing;
using NKeeper.Api.Contracts.Settings;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Controllers;

/// <summary>
/// How much a host may hold and how scans pre-select what they find. Everyone signed in reads them (the host editor
/// and scans follow them); administrators change them on the Settings page.
/// </summary>
[ApiController]
[Authorize]
public sealed class InventoryLimitsController(NKeeperDbContext db, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("api/settings/limits")]
    public async Task<InventoryLimitsResponse> Get(CancellationToken cancellationToken) =>
        InventoryLimitsResponse.From(await InventoryLimits.ReadAsync(db, cancellationToken));

    [HttpPut("api/admin/limits")]
    [Authorize(Roles = SystemRoles.Administrator)]
    [Audit("settings.limits", AuditTarget.None)]
    public async Task<InventoryLimitsResponse> Save(InventoryLimitsRequest request, CancellationToken cancellationToken)
    {
        var limits = await db.InventoryLimits.FirstOrDefaultAsync(l => l.Id == InventoryLimits.SingletonId, cancellationToken);
        if (limits is null)
        {
            limits = new InventoryLimits();
            db.InventoryLimits.Add(limits);
        }

        limits.MaxServicesPerHost = request.MaxServicesPerHost;
        limits.MaxCredentialsPerHost = request.MaxCredentialsPerHost;
        limits.MaxTagsPerHost = request.MaxTagsPerHost;
        limits.ScanUsersPreselectedUpTo = request.ScanUsersPreselectedUpTo;
        limits.UpdatedAtUtc = timeProvider.GetUtcNow();
        limits.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);
        return InventoryLimitsResponse.From(limits);
    }
}
