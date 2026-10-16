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

    /// <summary>
    /// For a new credential (no <see cref="Id"/>): start from the stored password and private key of
    /// this credential on another host the user can manage (cloning a host). Password, PrivateKey and
    /// the Clear flags still apply on top.
    /// </summary>
    public Guid? CopyFromCredentialId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(Label))
        {
            yield return new ValidationResult("Enter a username or a label.", ["username"]);
        }

        if (Id is not null && CopyFromCredentialId is not null)
        {
            yield return new ValidationResult("An existing credential can't be copied from another one.", ["copyFromCredentialId"]);
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

    /// <summary>A virtual machine (guest) rather than physical hardware.</summary>
    public bool IsVirtualMachine { get; init; }

    /// <summary>For a virtual machine: the host it runs on (a KVM or Proxmox node). Ignored for other hosts.</summary>
    public Guid? HypervisorId { get; init; }

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

/// <summary>Where a virtual machine runs, as a scan of its hypervisor found it.</summary>
public sealed record HypervisorLinkRequest
{
    public Guid GuestId { get; init; }

    public Guid HypervisorId { get; init; }

    /// <summary>The VM's identity on the hypervisor, e.g. libvirt:&lt;uuid&gt;; kept when omitted.</summary>
    [StringLength(128)]
    public string? VirtualMachineId { get; init; }
}

public sealed record HypervisorLinksRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(1000, ErrorMessage = "Link at most 1000 virtual machines at a time.")]
    public HypervisorLinkRequest[] Links { get; init; } = [];
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

/// <param name="HypervisorId">For a virtual machine: the host it runs on, when known.</param>
/// <param name="VirtualMachineId">The VM's identity on its hypervisor, recorded by scans (e.g. libvirt:&lt;uuid&gt;).</param>
/// <param name="HypervisorSeenAtUtc">When a scan last found the VM on that host; null when it was linked by hand.</param>
public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    string? Description,
    bool IsVirtualMachine,
    Guid? HypervisorId,
    string? VirtualMachineId,
    DateTimeOffset? HypervisorSeenAtUtc,
    IReadOnlyList<HostTagResponse> Tags,
    IReadOnlyList<HostServiceResponse> Services,
    IReadOnlyList<HostCredentialResponse> Credentials,
    IReadOnlyList<TrustedKeyResponse> TrustedKeys,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    uint Version,
    bool CanManage);

/// <summary>A tag to take off: one value of it, or (without <see cref="Value"/>) every value.</summary>
public sealed record BulkTagRemoval
{
    [Required(ErrorMessage = "Enter the tag name to remove.")]
    [StringLength(HostTag.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [StringLength(HostTag.ValueMaxLength)]
    public string? Value { get; init; }
}

/// <summary>Set (or, with no id, clear) the host the selected VMs run on.</summary>
public sealed record BulkHypervisorChange
{
    public Guid? HypervisorId { get; init; }
}

/// <summary>
/// The same changes to many hosts at once, applied in this order: <see cref="RemoveTags"/>,
/// <see cref="SetTags"/> (which replace every value of their names), <see cref="AddTags"/>, then
/// <see cref="Hypervisor"/>. Other details are left as they are.
/// </summary>
public sealed record BulkHostUpdateRequest : IValidatableObject
{
    [Required]
    [MinLength(1, ErrorMessage = "Select at least one host.")]
    [MaxLength(5000, ErrorMessage = "Change at most 5000 hosts at a time.")]
    public Guid[] HostIds { get; init; } = [];

    [MaxLength(50)]
    public HostTagRequest[] AddTags { get; init; } = [];

    [MaxLength(50)]
    public HostTagRequest[] SetTags { get; init; } = [];

    [MaxLength(50)]
    public BulkTagRemoval[] RemoveTags { get; init; } = [];

    /// <summary>Set: the hosts become virtual machines running on it. Null leaves them unchanged.</summary>
    public BulkHypervisorChange? Hypervisor { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AddTags.Length + SetTags.Length + RemoveTags.Length == 0 && Hypervisor is null)
        {
            yield return new ValidationResult("Choose a change to make.", ["addTags"]);
        }
    }
}
