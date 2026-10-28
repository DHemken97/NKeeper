using System.ComponentModel.DataAnnotations;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Transfer;

/*
 * Version 1 of what an instance export contains, inside the encrypted bundle. These records are a
 * contract of their own, not the EF entities: a later NKeeper must keep reading version 1 even after
 * the schema moves on. Ids are kept, so grants, hypervisor links and credential references survive.
 * Credential secrets are in plain text here; the bundle encrypts them, and the target re-encrypts them
 * with its own keys. Password hashes are Identity's own (salted PBKDF2), not reversible.
 */

/// <param name="Email">The SMTP settings, password in plain text; absent from older files.</param>
/// <param name="Zabbix">
/// The Zabbix settings, API token in plain text, from files written before Zabbix was a plugin.
/// Read into the zabbix plugin's settings; no longer written (they're in <paramref name="PluginSettings"/>).
/// </param>
/// <param name="PluginSettings">Every plugin's settings, secrets in plain text; absent from older files.</param>
public sealed record BundleV1(
    IReadOnlyList<BundleUser> Users,
    IReadOnlyList<BundleGrant> Grants,
    IReadOnlyList<BundleHost> Hosts,
    BundleEmail? Email = null,
    BundleZabbix? Zabbix = null,
    IReadOnlyList<BundlePluginSetting>? PluginSettings = null);

public sealed record BundleZabbix(bool Enabled, string Url, string? ApiToken, string? TrustedCertificateFingerprint);

/// <summary>One plugin setting: a <paramref name="Value"/>, or a <paramref name="Secret"/> in plain text (re-encrypted by the target).</summary>
public sealed record BundlePluginSetting(string PluginId, string Name, string? Value, string? Secret, DateTimeOffset UpdatedAtUtc);

public sealed record BundleEmail(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string? UserName,
    string? Password,
    string FromAddress,
    string? FromName);

/// <param name="IsDirectoryAccount">An Active Directory account (no local password). Absent from files written before AD sign-in, which means false.</param>
/// <param name="WorkspaceGroupBy">The user's saved host-tree grouping; absent from older files, which means none.</param>
/// <param name="WorkspaceSort">The user's saved host-tree sort; absent from older files, which means by name.</param>
/// <param name="SingleSignOnSubject">The identity provider's id for a single sign-on account (no local password); absent from older files.</param>
/// <param name="EmailTwoFactor">Also signs in with a code sent by email; absent from older files.</param>
public sealed record BundleUser(
    Guid Id,
    string UserName,
    string? Email,
    bool EmailConfirmed,
    string? PasswordHash,
    string? SecurityStamp,
    bool LockoutEnabled,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount,
    bool IsDisabled,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Roles,
    bool IsDirectoryAccount = false,
    IReadOnlyList<string>? WorkspaceGroupBy = null,
    string? WorkspaceSort = null,
    string? SingleSignOnSubject = null,
    bool EmailTwoFactor = false);

public sealed record BundleGrant(
    Guid Id,
    Guid UserId,
    Guid? HostId,
    string? TagName,
    string? TagValue,
    HostAccessLevel Level,
    DateTimeOffset CreatedAtUtc,
    Guid? CreatedByUserId);

/// <param name="JumpHostId">The host it's reached through; absent from older files.</param>
/// <param name="ZabbixHostId">The Zabbix host it's linked to, from files written before Zabbix was a plugin; read into the zabbix plugin's hostId.</param>
/// <param name="Plugins">Plugins' values on the host, by plugin ID then field; absent from older files.</param>
public sealed record BundleHost(
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
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? CreatedByUserId,
    Guid? UpdatedByUserId,
    IReadOnlyList<BundleService> Services,
    IReadOnlyList<BundleTag> Tags,
    IReadOnlyList<BundleCredential> Credentials,
    IReadOnlyList<BundleTrustedKey> TrustedKeys,
    Guid? JumpHostId = null,
    Guid? JumpCredentialId = null,
    string? ZabbixHostId = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Plugins = null);

public sealed record BundleService(Guid Id, ServiceProtocol Protocol, int? Port, string? Label, int Position, RdpSecurityMode? RdpSecurity, string? Address);

public sealed record BundleTag(Guid Id, string Name, string Value);

/// <param name="Password">Plain text, inside the encrypted bundle only.</param>
/// <param name="PrivateKey">Plain text, inside the encrypted bundle only.</param>
public sealed record BundleCredential(Guid Id, string? Label, string? Username, string? Domain, string? Password, string? PrivateKey, string? Notes, int Position);

public sealed record BundleTrustedKey(
    Guid Id, ServiceProtocol Protocol, int Port, string Algorithm, string Fingerprint, DateTimeOffset TrustedAtUtc, Guid? TrustedByUserId);

/// <summary>Export: the administrator's own password (to confirm it's them), and the passphrase to encrypt with.</summary>
public sealed record InstanceExportRequest
{
    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(1024)]
    public string Password { get; init; } = string.Empty;

    [Required(ErrorMessage = "Enter a passphrase.")]
    [StringLength(1024, MinimumLength = BundleFormat.MinimumPassphraseLength, ErrorMessage = "The passphrase must be at least 16 characters.")]
    public string Passphrase { get; init; } = string.Empty;

    /// <summary>Stop changes to hosts and users here once the file is made, so none are left behind.</summary>
    public bool LockAfterExport { get; init; }
}

/// <param name="CanImport">Whether this instance can receive an import: it has no hosts and no users but the caller.</param>
/// <param name="ImportBlockedReason">Why not, when it can't.</param>
/// <param name="Locked">Whether hosts and users are locked because the data was exported.</param>
public sealed record TransferStatusResponse(
    bool CanImport,
    string? ImportBlockedReason,
    bool Locked,
    DateTimeOffset? LockedAtUtc,
    string? LockedBy,
    string Version);

/// <summary>What a bundle holds (preview), or what an import brought in.</summary>
public sealed record ImportSummaryResponse(
    string ExportedBy,
    DateTimeOffset ExportedAtUtc,
    string SourceVersion,
    int Users,
    int Administrators,
    int Grants,
    int Hosts,
    int Credentials,
    int TrustedKeys,
    IReadOnlyList<string> Warnings,
    bool Imported);
