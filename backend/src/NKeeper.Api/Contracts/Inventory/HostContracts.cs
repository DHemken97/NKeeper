using System.ComponentModel.DataAnnotations;
using NKeeper.Api.Inventory;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Inventory;

public sealed record HostServiceRequest : IValidatableObject
{
    [Required(ErrorMessage = "Choose a service type.")]
    public ServiceProtocol? Protocol { get; init; }

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int? Port { get; init; }

    [StringLength(100)]
    public string? Label { get; init; }

    /// <summary>RDP services only; defaults to <see cref="RdpSecurityMode.Auto"/>.</summary>
    public RdpSecurityMode? RdpSecurity { get; init; }

    /// <summary>iDRAC services only (required there): the iDRAC's own IP address or hostname. Ignored for other services.</summary>
    [StringLength(253)]
    public string? Address { get; init; }

    /// <summary>SQL Server services only: the named instance, or empty for the default instance. Ignored for other services.</summary>
    [StringLength(HostService.InstanceMaxLength, ErrorMessage = "Instance names are at most 16 characters.")]
    public string? Instance { get; init; }

    /// <summary>Which of the service's tools the menus offer; left out for all of them. Ignored for services without tools.</summary>
    [MaxLength(16)]
    public IReadOnlyList<string>? Tools { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var known = ServiceTools.For(Protocol);
        if (known.Count > 0 && Tools?.FirstOrDefault(tool => !known.Contains(tool)) is { } unknown)
        {
            yield return new ValidationResult($"“{unknown}” isn't a tool of this service.", [nameof(Tools)]);
        }
    }
}

/// <summary>A <c>name: value</c> tag. The value is optional.</summary>
public sealed record HostTagRequest : IValidatableObject
{
    [Required(ErrorMessage = "Enter a tag name.")]
    [StringLength(HostTag.NameMaxLength, ErrorMessage = "Tag names can be at most 64 characters.")]
    public string Name { get; init; } = string.Empty;

    [StringLength(HostTag.ValueMaxLength, ErrorMessage = "Tag values can be at most 255 characters.")]
    public string? Value { get; init; }

    /// <summary>Whether VMs running on the host inherit the tag; defaults to true.</summary>
    public bool Inheritable { get; init; } = true;

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
    /// The services (positions in the request's service list) this credential is used with; null for
    /// all of them, the default. Empty means none.
    /// </summary>
    [MaxLength(20)]
    public int[]? ServiceIndexes { get; init; }

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

    /// <summary>A host with an SSH service to reach this one through (a bastion).</summary>
    public Guid? JumpHostId { get; init; }

    /// <summary>The jump host's credential to use; null means its first usable one.</summary>
    public Guid? JumpCredentialId { get; init; }

    /// <summary>Logical processors, or a VM's vCPUs.</summary>
    [Range(1, 100_000, ErrorMessage = "CPU cores must be between 1 and 100000.")]
    public int? CpuCores { get; init; }

    /// <summary>Memory in MiB.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Enter the memory as a positive amount.")]
    public int? MemoryMiB { get; init; }

    [StringLength(128)]
    public string? SerialNumber { get; init; }

    /// <summary>The host's ID in Zabbix, to show what Zabbix knows about it; null when it isn't linked.</summary>
    [StringLength(NKeeper.Infrastructure.Inventory.Host.ZabbixHostIdMaxLength, ErrorMessage = "A Zabbix host ID has at most 20 digits.")]
    [RegularExpression(@"^\s*[0-9]+\s*$", ErrorMessage = "A Zabbix host ID is a number, e.g. 10084.")]
    public string? ZabbixHostId { get; init; }

    [MaxLength(InventoryLimits.CeilingTags, ErrorMessage = "A host can have at most 500 tags.")]
    public HostTagRequest[] Tags { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingServices, ErrorMessage = "A host can have at most 1000 services.")]
    public HostServiceRequest[] Services { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingCredentials, ErrorMessage = "A host can have at most 1000 credentials.")]
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

        for (var index = 0; index < Services.Length; index++)
        {
            if (Services[index].Protocol is { } protocol && !SupportedServices.IsSupported(protocol))
            {
                yield return new ValidationResult($"NKeeper can't connect with {protocol} services, so they can't be added.", [$"services[{index}].protocol"]);
            }

            if (Services[index].Protocol == ServiceProtocol.Ssms
                && SqlServerInstance.Normalize(Services[index].Instance) is { } instance
                && !SqlServerInstance.IsValid(instance))
            {
                yield return new ValidationResult(
                    "Instance names start with a letter or underscore, and contain only letters, digits, _, $ and #.", [$"services[{index}].instance"]);
            }

            if (Services[index].Protocol == ServiceProtocol.Oracle)
            {
                var service = Services[index].Instance?.Trim();
                if (string.IsNullOrEmpty(service))
                {
                    yield return new ValidationResult("Enter the database's service name (e.g. ORCLPDB1, or its SID).", [$"services[{index}].instance"]);
                }
                else if (!SqlServerInstance.IsValidOracleService(service))
                {
                    yield return new ValidationResult("Service names start with a letter, and contain only letters, digits, _, $, #, . and -.", [$"services[{index}].instance"]);
                }
            }

            if (Services[index].Protocol != ServiceProtocol.Idrac)
            {
                continue;
            }

            var address = Services[index].Address?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                yield return new ValidationResult("Enter the iDRAC's IP address.", [$"services[{index}].address"]);
            }
            else if (!NetworkAddress.TryNormalizeIp(address, out _) && !NetworkAddress.IsValidHostname(address))
            {
                yield return new ValidationResult("Enter a valid IP address or hostname for the iDRAC.", [$"services[{index}].address"]);
            }
        }

        for (var index = 0; index < Credentials.Length; index++)
        {
            if (Credentials[index].ServiceIndexes is { } chosen && (chosen.Any(i => i < 0 || i >= Services.Length) || chosen.Distinct().Count() != chosen.Length))
            {
                yield return new ValidationResult("Choose services the host has, each once.", [$"credentials[{index}].serviceIndexes"]);
            }
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

    /// <summary>The vCPUs the hypervisor gives it; kept when omitted.</summary>
    [Range(1, 100_000)]
    public int? CpuCores { get; init; }

    /// <summary>The memory the hypervisor gives it, in MiB; kept when omitted.</summary>
    [Range(1, int.MaxValue)]
    public int? MemoryMiB { get; init; }
}

public sealed record HypervisorLinksRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(1000, ErrorMessage = "Link at most 1000 virtual machines at a time.")]
    public HypervisorLinkRequest[] Links { get; init; } = [];
}

/// <param name="Inheritable">Whether VMs running on the host inherit the tag.</param>
public sealed record HostTagResponse(string Name, string? Value, bool Inheritable = true);

/// <summary>
/// A connection method. <see cref="RdpSecurity"/> is set for RDP services only, <see cref="Address"/>
/// for services with their own address (iDRAC) only, <see cref="Instance"/> for SQL Server's named instances only.
/// </summary>
/// <param name="Tools">Which of its tools the menus offer; null for all of them.</param>
public sealed record HostServiceResponse(ServiceProtocol Protocol, int? Port, string? Label, RdpSecurityMode? RdpSecurity, string? Address = null, string? Instance = null, Guid? Id = null, IReadOnlyList<string>? Tools = null);

/// <summary>A credential as seen by clients. Secret values are never included.</summary>
public sealed record HostCredentialResponse(
    Guid Id,
    string? Label,
    string? Username,
    string? Domain,
    string? Notes,
    bool HasPassword,
    bool HasPrivateKey,
    IReadOnlyList<Guid>? ServiceIds = null);

/// <summary>An SSH host key or RDP certificate trusted for one of the host's ports.</summary>
public sealed record TrustedKeyResponse(ServiceProtocol Protocol, int Port, string Algorithm, string Fingerprint, DateTimeOffset TrustedAtUtc);

/// <param name="HypervisorId">For a virtual machine: the host it runs on, when known.</param>
/// <param name="VirtualMachineId">The VM's identity on its hypervisor, recorded by scans (e.g. libvirt:&lt;uuid&gt;).</param>
/// <param name="HypervisorSeenAtUtc">When a scan last found the VM on that host; null when it was linked by hand.</param>
/// <param name="CpuCores">Logical processors, or a VM's vCPUs.</param>
/// <param name="MemoryMiB">Memory in MiB.</param>
/// <param name="ZabbixHostId">The host's ID in Zabbix, when it's linked to one.</param>
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
    int? CpuCores,
    int? MemoryMiB,
    string? SerialNumber,
    Guid? JumpHostId,
    Guid? JumpCredentialId,
    string? ZabbixHostId,
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

/// <summary>A service to take off: the one on that port, or (without <see cref="Port"/>) every one of that type.</summary>
public sealed record BulkServiceRemoval
{
    [Required(ErrorMessage = "Choose the service type to remove.")]
    public ServiceProtocol? Protocol { get; init; }

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int? Port { get; init; }
}

/// <summary>
/// Which credentials of a host: by <see cref="Username"/> (and <see cref="Domain"/>, any domain when
/// omitted) and/or <see cref="Label"/>, compared without regard to case.
/// </summary>
public record BulkCredentialMatch : IValidatableObject
{
    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(256)]
    public string? Domain { get; init; }

    [StringLength(100)]
    public string? Label { get; init; }

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(Label))
        {
            yield return new ValidationResult("Enter the username or label of the credential.", ["username"]);
        }
    }
}

/// <summary>New secrets for the matching credentials: set (or clear) the password and/or private key.</summary>
public sealed record BulkCredentialUpdate : BulkCredentialMatch
{
    [StringLength(1024)]
    public string? Password { get; init; }

    public bool ClearPassword { get; init; }

    [StringLength(16_384)]
    public string? PrivateKey { get; init; }

    public bool ClearPrivateKey { get; init; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (string.IsNullOrEmpty(Password) && !ClearPassword && string.IsNullOrEmpty(PrivateKey) && !ClearPrivateKey)
        {
            yield return new ValidationResult("Enter a new password or private key, or clear one.", ["password"]);
        }
    }
}

/// <summary>
/// Takes services of these types off the matching credentials (e.g. SAP ASE off every "root"), which are then
/// used with the host's other services. A credential used with all services gets the list of the others.
/// </summary>
public sealed record BulkCredentialServiceRemoval : BulkCredentialMatch
{
    [Required]
    [MaxLength(50)]
    public ServiceProtocol[] Protocols { get; init; } = [];

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (Protocols.Length == 0)
        {
            yield return new ValidationResult("Tick the services to take off the credential.", ["protocols"]);
        }
    }
}

/// <summary>Set (or, with no id, clear) the host the selected VMs run on.</summary>
public sealed record BulkHypervisorChange
{
    public Guid? HypervisorId { get; init; }
}

/// <summary>
/// The same changes to many hosts at once, applied in this order: <see cref="RemoveTags"/>,
/// <see cref="SetTags"/> (which replace every value of their names), <see cref="AddTags"/>,
/// <see cref="Hypervisor"/>, <see cref="RemoveServices"/>, <see cref="AddServices"/>, then
/// <see cref="RemoveCredentials"/>, <see cref="UpdateCredentials"/>, <see cref="RemoveCredentialServices"/> and <see cref="AddCredentials"/>.
/// Other details are left as they are.
/// </summary>
public sealed record BulkHostUpdateRequest : IValidatableObject
{
    [Required]
    [MinLength(1, ErrorMessage = "Select at least one host.")]
    [MaxLength(5000, ErrorMessage = "Change at most 5000 hosts at a time.")]
    public Guid[] HostIds { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingTags)]
    public HostTagRequest[] AddTags { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingTags)]
    public HostTagRequest[] SetTags { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingTags)]
    public BulkTagRemoval[] RemoveTags { get; init; } = [];

    /// <summary>Set: the hosts become virtual machines running on it. Null leaves them unchanged.</summary>
    public BulkHypervisorChange? Hypervisor { get; init; }

    [MaxLength(InventoryLimits.CeilingServices)]
    public BulkServiceRemoval[] RemoveServices { get; init; } = [];

    /// <summary>Services each host gets, after its own; a host that already has one (same type and port) keeps it as it is.</summary>
    [MaxLength(InventoryLimits.CeilingServices)]
    public HostServiceRequest[] AddServices { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingCredentials)]
    public BulkCredentialMatch[] RemoveCredentials { get; init; } = [];

    [MaxLength(InventoryLimits.CeilingCredentials)]
    public BulkCredentialUpdate[] UpdateCredentials { get; init; } = [];

    /// <summary>Services of these types are taken off the matching credentials.</summary>
    [MaxLength(InventoryLimits.CeilingCredentials)]
    public BulkCredentialServiceRemoval[] RemoveCredentialServices { get; init; } = [];

    /// <summary>Credentials each host gets, after its own; a host that already has the account (or label) keeps its own.</summary>
    [MaxLength(InventoryLimits.CeilingCredentials)]
    public HostCredentialRequest[] AddCredentials { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var credentialChanges = RemoveCredentials.Length + UpdateCredentials.Length + RemoveCredentialServices.Length + AddCredentials.Length;
        if (AddTags.Length + SetTags.Length + RemoveTags.Length + AddServices.Length + RemoveServices.Length + credentialChanges == 0 && Hypervisor is null)
        {
            yield return new ValidationResult("Choose a change to make.", ["addTags"]);
        }

        for (var index = 0; index < AddServices.Length; index++)
        {
            if (AddServices[index].Protocol is not { } protocol)
            {
                continue;
            }

            if (!SupportedServices.IsSupported(protocol))
            {
                yield return new ValidationResult($"NKeeper can't connect with {protocol} services, so they can't be added.", [$"addServices[{index}].protocol"]);
            }
            else if (protocol == ServiceProtocol.Idrac)
            {
                // Each iDRAC has its own address, so one can't be given to many hosts.
                yield return new ValidationResult("Add iDRAC services one host at a time: each has its own address.", [$"addServices[{index}].protocol"]);
            }
        }

        for (var index = 0; index < AddCredentials.Length; index++)
        {
            if (AddCredentials[index].Id is not null || AddCredentials[index].CopyFromCredentialId is not null)
            {
                yield return new ValidationResult("New credentials can't name or copy an existing one.", [$"addCredentials[{index}]"]);
            }
        }
    }
}

/// <summary>Duplicates of a host to combine into it; they're deleted afterwards.</summary>
public sealed record MergeHostsRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "Choose the other hosts to combine into this one.")]
    [MaxLength(NKeeper.Api.Inventory.HostMerger.MaxSources, ErrorMessage = "Combine at most 50 hosts at a time.")]
    public Guid[] SourceIds { get; init; } = [];
}

/// <summary>A host to add for a VM or container a scan of its hypervisor found.</summary>
public sealed record ScannedHostRequest
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

    /// <summary>The host it runs on.</summary>
    public Guid HypervisorId { get; init; }

    /// <summary>Its identity on the hypervisor, e.g. libvirt:&lt;uuid&gt;.</summary>
    [StringLength(128)]
    public string? VirtualMachineId { get; init; }

    /// <summary>The vCPUs the hypervisor gives it.</summary>
    [Range(1, 100_000)]
    public int? CpuCores { get; init; }

    /// <summary>The memory the hypervisor gives it, in MiB.</summary>
    [Range(1, int.MaxValue)]
    public int? MemoryMiB { get; init; }
}

public sealed record ScannedHostsRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(1000, ErrorMessage = "Add at most 1000 hosts at a time.")]
    public ScannedHostRequest[] Hosts { get; init; } = [];
}
