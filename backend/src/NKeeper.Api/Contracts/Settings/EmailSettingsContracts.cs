using System.ComponentModel.DataAnnotations;
using NKeeper.Infrastructure.Email;

namespace NKeeper.Api.Contracts.Settings;

/// <summary>The saved SMTP settings. The password itself is never returned, only whether there is one.</summary>
public sealed record EmailSettingsResponse(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string? UserName,
    bool HasPassword,
    string FromAddress,
    string? FromName,
    DateTimeOffset? UpdatedAtUtc);

/// <param name="SentTo">The signed-in administrator's address the test went to.</param>
public sealed record EmailTestResponse(string SentTo);

public sealed record EmailSettingsRequest : IValidatableObject
{
    public bool Enabled { get; init; }

    [StringLength(EmailSettings.HostMaxLength)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "Enter a port from 1 to 65535.")]
    public int Port { get; init; } = 587;

    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    [StringLength(EmailSettings.AddressMaxLength)]
    public string? UserName { get; init; }

    /// <summary>A new password; null keeps the saved one.</summary>
    [StringLength(512)]
    public string? Password { get; init; }

    /// <summary>Forget the saved password (when <see cref="Password"/> is null).</summary>
    public bool ClearPassword { get; init; }

    [StringLength(EmailSettings.AddressMaxLength)]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string FromAddress { get; init; } = string.Empty;

    [StringLength(128)]
    public string? FromName { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Saving it off may leave it half-filled; turning it on (or testing it) needs a server and a sender.
        if (Enabled && string.IsNullOrWhiteSpace(Host))
        {
            yield return new ValidationResult("Enter the SMTP server.", [nameof(Host)]);
        }

        if (Enabled && string.IsNullOrWhiteSpace(FromAddress))
        {
            yield return new ValidationResult("Enter the address emails come from.", [nameof(FromAddress)]);
        }
    }
}
