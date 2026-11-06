using System.ComponentModel.DataAnnotations;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Settings;

/// <summary>The limits in force (the defaults until an administrator saves some), and the most each may be raised to.</summary>
public sealed record InventoryLimitsResponse(
    int MaxServicesPerHost,
    int MaxCredentialsPerHost,
    int MaxTagsPerHost,
    int ScanUsersPreselectedUpTo,
    int ZipReserveGigabytes,
    DateTimeOffset? UpdatedAtUtc)
{
    public int CeilingServices => InventoryLimits.CeilingServices;

    public int CeilingCredentials => InventoryLimits.CeilingCredentials;

    public int CeilingTags => InventoryLimits.CeilingTags;

    public int CeilingScanUsers => InventoryLimits.CeilingScanUsers;

    public int CeilingZipReserveGigabytes => InventoryLimits.CeilingZipReserveGigabytes;

    public static InventoryLimitsResponse From(InventoryLimits limits) =>
        new(limits.MaxServicesPerHost, limits.MaxCredentialsPerHost, limits.MaxTagsPerHost, limits.ScanUsersPreselectedUpTo, limits.ZipReserveGigabytes, limits.UpdatedAtUtc);
}

public sealed record InventoryLimitsRequest
{
    [Range(1, InventoryLimits.CeilingServices, ErrorMessage = "Between 1 and 1000 services.")]
    public int MaxServicesPerHost { get; init; } = InventoryLimits.DefaultMaxServicesPerHost;

    [Range(1, InventoryLimits.CeilingCredentials, ErrorMessage = "Between 1 and 1000 credentials.")]
    public int MaxCredentialsPerHost { get; init; } = InventoryLimits.DefaultMaxCredentialsPerHost;

    [Range(1, InventoryLimits.CeilingTags, ErrorMessage = "Between 1 and 500 tags.")]
    public int MaxTagsPerHost { get; init; } = InventoryLimits.DefaultMaxTagsPerHost;

    /// <summary>0: found users always start unticked.</summary>
    [Range(0, InventoryLimits.CeilingScanUsers, ErrorMessage = "Between 0 and 1000 users.")]
    public int ScanUsersPreselectedUpTo { get; init; } = InventoryLimits.DefaultScanUsersPreselectedUpTo;

    /// <summary>0: a folder may use all the free space.</summary>
    [Range(0, InventoryLimits.CeilingZipReserveGigabytes, ErrorMessage = "Between 0 and 1000 GB.")]
    public int ZipReserveGigabytes { get; init; } = InventoryLimits.DefaultZipReserveGigabytes;
}
