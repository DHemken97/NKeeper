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

    /// <summary>RDP services only; defaults to <see cref="RdpSecurityMode.Auto"/>.</summary>
    public RdpSecurityMode? RdpSecurity { get; init; }
}

/// <summary>A <c>name: value</c> tag. The value is optional.</summary>
public sealed record HostTagRequest : IValidatableObject
{
    [Required(ErrorMessage = "Enter a tag name.")]
    [StringLength(HostTag.NameMaxLength, ErrorMessage = "Tag names can be at most 64 characters.")]
    public string Name { get; init; } = string.Empty;

    [StringLength(HostTag.ValueMaxLength, ErrorMessage = "Tag values can be at most 255 characters.")]
    public string? Value { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // "name:value" is the search syntax, so a colon in a name would be ambiguous.
        if (Name.Contains(':', StringComparison.Ordinal))
        {
            yield return new ValidationResult("Tag names cannot contain a colon.", ["name"]);
        }
    }
}

/// <summary>
/// A credential belonging to the host. Send <see cref="Id"/> to update an existing credential,
/// omit it to add a new one; credentials left out of the request are deleted.
/// Secrets are write-only: omit <see cref="Password"/> or <see cref="PrivateKey"/> to keep the
/// stored value, or set the matching Clear flag to remove it.
/// </summary>
public sealed record HostCredentialRequest : IValidatableObject
{
    public Guid? Id { get; init; }

    [StringLength(100)]
    public string? Label { get; init; }

    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(256)]
    public string? Domain { get; init; }

    [StringLength(1024)]
    public string? Password { get; init; }

    public bool ClearPassword { get; init; }

    [StringLength(16_384)]
    public string? PrivateKey { get; init; }

    public bool ClearPrivateKey { get; init; }

    [StringLength(2000)]
    public string? Notes { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(Label))
        {
            yield return new ValidationResult("Enter a username or a label.", ["username"]);
        }
    }
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

    [MaxLength(50, ErrorMessage = "A host can have at most 50 tags.")]
    public HostTagRequest[] Tags { get; init; } = [];

    [MaxLength(20, ErrorMessage = "A host can have at most 20 services.")]
    public HostServiceRequest[] Services { get; init; } = [];

    [MaxLength(20, ErrorMessage = "A host can have at most 20 credentials.")]
    public HostCredentialRequest[] Credentials { get; init; } = [];

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

        var duplicateIds = Credentials.Where(c => c.Id is not null).GroupBy(c => c.Id).Any(g => g.Count() > 1);
        if (duplicateIds)
        {
            yield return new ValidationResult("Each credential can only appear once.", ["credentials"]);
        }
    }
}

public sealed record HostTagResponse(string Name, string? Value);

/// <summary>A connection method. <see cref="RdpSecurity"/> is set for RDP services only.</summary>
public sealed record HostServiceResponse(ServiceProtocol Protocol, int? Port, string? Label, RdpSecurityMode? RdpSecurity);

/// <summary>A credential as seen by clients. Secret values are never included.</summary>
public sealed record HostCredentialResponse(
    Guid Id,
    string? Label,
    string? Username,
    string? Domain,
    string? Notes,
    bool HasPassword,
    bool HasPrivateKey);

/// <summary>An SSH host key or RDP certificate trusted for one of the host's ports.</summary>
public sealed record TrustedKeyResponse(ServiceProtocol Protocol, int Port, string Algorithm, string Fingerprint, DateTimeOffset TrustedAtUtc);

public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    string? Description,
    IReadOnlyList<HostTagResponse> Tags,
    IReadOnlyList<HostServiceResponse> Services,
    IReadOnlyList<HostCredentialResponse> Credentials,
    IReadOnlyList<TrustedKeyResponse> TrustedKeys,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    uint Version);
