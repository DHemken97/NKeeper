using System.ComponentModel.DataAnnotations;
using NKeeper.Api.Inventory;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Inventory;

public sealed record HostServiceRequest
{
    [Required(ErrorMessage = "Choose a service type.")]
    public ServiceProtocol? Protocol { get; init; }

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int? Port { get; init; }

    [StringLength(100)]
    public string? Label { get; init; }
}

/// <summary>Create/replace a host. Collections replace the existing values entirely.</summary>
public sealed record HostRequest : IValidatableObject
{
    [Required(ErrorMessage = "Enter a name.")]
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(253)]
    public string? Hostname { get; init; }

    [StringLength(45)]
    public string? IpAddress { get; init; }

    [StringLength(32)]
    public string? MacAddress { get; init; }

    [StringLength(4000)]
    public string? Description { get; init; }

    public Guid? GroupId { get; init; }

    [MaxLength(20, ErrorMessage = "A host can have at most 20 tags.")]
    public string[] Tags { get; init; } = [];

    [MaxLength(20, ErrorMessage = "A host can have at most 20 services.")]
    public HostServiceRequest[] Services { get; init; } = [];

    [MaxLength(50, ErrorMessage = "A host can have at most 50 credentials.")]
    public Guid[] CredentialIds { get; init; } = [];

    /// <summary>The <c>version</c> from the last read. Required when updating.</summary>
    public uint? Version { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Member names are camelCase to match the JSON property names used for all other errors.
        var hasHostname = !string.IsNullOrWhiteSpace(Hostname);
        var hasIp = !string.IsNullOrWhiteSpace(IpAddress);

        if (!hasHostname && !hasIp)
        {
            yield return new ValidationResult("Enter a hostname or an IP address.", ["hostname"]);
        }

        if (hasHostname && !NetworkAddress.IsValidHostname(Hostname!))
        {
            yield return new ValidationResult("Enter a valid hostname, e.g. server01.example.com.", ["hostname"]);
        }

        if (hasIp && !NetworkAddress.TryNormalizeIp(IpAddress!, out _))
        {
            yield return new ValidationResult("Enter a valid IPv4 or IPv6 address.", ["ipAddress"]);
        }

        if (!string.IsNullOrWhiteSpace(MacAddress) && !NetworkAddress.TryNormalizeMac(MacAddress, out _))
        {
            yield return new ValidationResult("Enter a MAC address like AA:BB:CC:DD:EE:FF.", ["macAddress"]);
        }

        if (Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > 50))
        {
            yield return new ValidationResult("Tags must be 1 to 50 characters.", ["tags"]);
        }
    }
}

public enum HostSortField
{
    Name,
    Address,
    Group,
    Updated,
}

public enum SortDirection
{
    Asc,
    Desc,
}

public sealed record HostListQuery
{
    /// <summary>Space-separated terms; every term must match name, address, MAC, description, group or a tag.</summary>
    [StringLength(200)]
    public string? Search { get; init; }

    public Guid? GroupId { get; init; }

    /// <summary>Only hosts without a group. Ignored when <see cref="GroupId"/> is set.</summary>
    public bool Ungrouped { get; init; }

    [StringLength(50)]
    public string? Tag { get; init; }

    public ServiceProtocol? Protocol { get; init; }

    public HostSortField Sort { get; init; } = HostSortField.Name;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    [Range(1, 100_000)]
    public int Page { get; init; } = 1;

    [Range(1, 200)]
    public int PageSize { get; init; } = 50;
}

public sealed record GroupReference(Guid Id, string Name);

public sealed record CredentialReference(Guid Id, string Name, string? Username, string? Domain);

public sealed record HostServiceResponse(ServiceProtocol Protocol, int? Port, string? Label);

public sealed record HostSummaryResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    GroupReference? Group,
    IReadOnlyList<string> Tags,
    IReadOnlyList<HostServiceResponse> Services,
    int CredentialCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    string? Description,
    GroupReference? Group,
    IReadOnlyList<string> Tags,
    IReadOnlyList<HostServiceResponse> Services,
    IReadOnlyList<CredentialReference> Credentials,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    uint Version);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
