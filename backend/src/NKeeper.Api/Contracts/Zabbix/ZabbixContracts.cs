using System.ComponentModel.DataAnnotations;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Controllers;
using NKeeper.Api.Zabbix;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Zabbix;

namespace NKeeper.Api.Contracts.Zabbix;

/// <summary>The saved Zabbix settings. The API token itself is never returned, only whether there is one.</summary>
/// <param name="TrustedCertificateFingerprint">The server's certificate an administrator trusted, when it isn't publicly valid.</param>
public sealed record ZabbixSettingsResponse(
    bool Enabled,
    string Url,
    bool HasApiToken,
    string? TrustedCertificateFingerprint,
    DateTimeOffset? UpdatedAtUtc);

public sealed record ZabbixSettingsRequest : IValidatableObject
{
    public bool Enabled { get; init; }

    /// <summary>The Zabbix frontend's address, e.g. https://zabbix.example.com/zabbix.</summary>
    [StringLength(ZabbixSettings.UrlMaxLength)]
    public string Url { get; init; } = string.Empty;

    /// <summary>A new API token; null keeps the saved one.</summary>
    [StringLength(512)]
    public string? ApiToken { get; init; }

    /// <summary>
    /// The server's certificate fingerprint the administrator chose to trust after a test answered
    /// "trustRequired". Null keeps the trusted one, unless the server's address changed.
    /// </summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Saving it off may leave it half-filled; turning it on needs an address.
        if (Enabled && string.IsNullOrWhiteSpace(Url))
        {
            yield return new ValidationResult("Enter the Zabbix address.", [nameof(Url)]);
        }
        else if (!string.IsNullOrWhiteSpace(Url) && !ZabbixUrl.TryNormalize(Url, out _))
        {
            yield return new ValidationResult("Enter the Zabbix address, e.g. https://zabbix.example.com/zabbix.", [nameof(Url)]);
        }
    }
}

/// <summary>
/// A test of the settings: either the server answered (<see cref="Version"/>, and how many hosts the
/// token can see), or its certificate needs to be trusted first.
/// </summary>
public sealed record ZabbixTestResponse(string Status, string? Version, int? HostCount, WebCertificateResponse? Certificate);

/// <summary>Whether Zabbix is set up, for every signed-in user: the UI shows Zabbix information only when it is.</summary>
/// <param name="FrontendUrl">The Zabbix frontend's address, for "Open in Zabbix" links.</param>
public sealed record ZabbixStatusResponse(bool Enabled, string? FrontendUrl);

/// <param name="Type">Agent, SNMP, IPMI or JMX.</param>
/// <param name="Availability">Available, Unavailable or Unknown.</param>
public sealed record ZabbixInterfaceResponse(string Type, string? IpAddress, string? Dns, string Port, bool UseIp, bool Main, string Availability, string? Error);

/// <summary>A host in Zabbix, as listed by searches and the comparison.</summary>
/// <param name="HostId">Zabbix's ID for it.</param>
/// <param name="Host">Its technical name.</param>
/// <param name="Name">Its visible name.</param>
/// <param name="MacAddresses">From its inventory (MAC address A and B).</param>
public sealed record ZabbixHostSummary(
    string HostId,
    string Host,
    string Name,
    bool Monitored,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<string> MacAddresses,
    string? OperatingSystem,
    IReadOnlyList<HostTagResponse> Tags);

/// <summary>A value Zabbix collected (an item's latest value).</summary>
/// <param name="Units">As Zabbix stores them, e.g. "%", "B", "uptime".</param>
public sealed record ZabbixItemResponse(string Key, string Name, string? Value, string? Units, DateTimeOffset? CheckedAtUtc);

/// <param name="Severity">0 not classified, 1 information, 2 warning, 3 average, 4 high, 5 disaster.</param>
public sealed record ZabbixProblemResponse(string EventId, string Name, int Severity, DateTimeOffset StartedAtUtc, bool Acknowledged, bool Suppressed);

/// <summary>What Zabbix knows about one host.</summary>
/// <param name="Availability">The host's overall availability from its interfaces: Available, Unavailable, Unknown or Mixed.</param>
/// <param name="Inventory">Its inventory fields that have a value, by Zabbix field name (e.g. os, serialno_a), or empty when inventory is off.</param>
/// <param name="Items">The latest values of well-known items (CPU, memory, uptime, OS…) the host has.</param>
/// <param name="Problems">Its unresolved problems, newest first.</param>
public sealed record ZabbixHostOverview(
    ZabbixHostSummary Host,
    string? Description,
    bool InMaintenance,
    string Availability,
    IReadOnlyList<ZabbixInterfaceResponse> Interfaces,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Inventory,
    IReadOnlyList<ZabbixItemResponse> Items,
    IReadOnlyList<ZabbixProblemResponse> Problems);

/// <summary>An NKeeper host on one side of the comparison.</summary>
public sealed record ComparedHost(Guid Id, string Name, string? Hostname, string? IpAddress, string? MacAddress, bool CanManage);

/// <summary>An NKeeper host and the Zabbix host it's linked to, or (for a probable match) looks like.</summary>
/// <param name="MatchedBy">For a probable match: mac, ip, hostname or name.</param>
public sealed record ZabbixPair(ComparedHost Host, ZabbixHostSummary Zabbix, string? MatchedBy);

/// <summary>
/// Zabbix next to NKeeper: hosts linked to each other, hosts that look like the same machine but
/// aren't linked yet (by MAC, then IP, then hostname, then name), and hosts only one side has.
/// </summary>
/// <param name="BrokenLinks">NKeeper hosts linked to a Zabbix host that no longer exists.</param>
public sealed record ZabbixComparisonResponse(
    IReadOnlyList<ZabbixPair> Linked,
    IReadOnlyList<ZabbixPair> ProbableMatches,
    IReadOnlyList<ZabbixHostSummary> ZabbixOnly,
    IReadOnlyList<ComparedHost> NKeeperOnly,
    IReadOnlyList<ComparedHost> BrokenLinks);

/// <summary>Link (or, with no Zabbix host ID, unlink) an NKeeper host.</summary>
public sealed record ZabbixLinkRequest
{
    public Guid HostId { get; init; }

    [StringLength(NKeeper.Infrastructure.Inventory.Host.ZabbixHostIdMaxLength)]
    [RegularExpression("^[0-9]+$", ErrorMessage = "A Zabbix host ID is a number.")]
    public string? ZabbixHostId { get; init; }
}

public sealed record ZabbixLinksRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(5000, ErrorMessage = "Link at most 5000 hosts at a time.")]
    public ZabbixLinkRequest[] Links { get; init; } = [];
}

/// <summary>Add NKeeper hosts for these Zabbix hosts, from what Zabbix knows about them.</summary>
public sealed record ZabbixImportRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "Choose at least one Zabbix host.")]
    [MaxLength(1000, ErrorMessage = "Add at most 1000 hosts at a time.")]
    public string[] ZabbixHostIds { get; init; } = [];

    /// <summary>Also add each Zabbix host group as a <c>group: name</c> tag.</summary>
    public bool GroupsAsTags { get; init; }
}

/// <summary>A Zabbix host that wasn't added, and why.</summary>
public sealed record ZabbixImportSkipped(string ZabbixHostId, string Name, string Reason);

public sealed record ZabbixImportResponse(IReadOnlyList<HostResponse> Created, IReadOnlyList<ZabbixImportSkipped> Skipped);
