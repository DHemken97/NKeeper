using System.ComponentModel.DataAnnotations;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;

namespace NKeeper.Server.Controllers;

public sealed record LoginRequest(
    [Required, MaxLength(256)] string Username,
    [Required, MaxLength(1024)] string Password,
    AuthDriverType Driver = AuthDriverType.LocalDatabase);

public sealed record VerifyTotpRequest(
    [Required] string MfaToken,
    [Required, MaxLength(16)] string Code);

public sealed record BeginTotpEnrollmentRequest([MaxLength(128)] string? Name);

public sealed record ConfirmTotpRequest(
    [Required] Guid CredentialId,
    [Required, MaxLength(16)] string Code);

public enum LoginStatus
{
    Authenticated,
    MfaRequired
}

public sealed record LoginResponse(
    LoginStatus Status,
    string? AccessToken = null,
    DateTime? ExpiresAtUtc = null,
    UserInfo? User = null,
    string? MfaToken = null,
    IReadOnlyList<string>? MfaMethods = null);

public sealed record MfaCredentialInfo(Guid Id, MfaCredentialType Type, string Name, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc);

public sealed record UserInfo(
    Guid Id,
    string Username,
    string? DisplayName,
    string? Email,
    AuthDriverType AuthSource,
    bool IsAdmin,
    IReadOnlyList<MfaCredentialInfo> MfaCredentials)
{
    public static UserInfo From(User user) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Email,
        user.AuthSource,
        user.IsAdmin,
        user.MfaCredentials
            .Where(c => c.IsConfirmed)
            .Select(c => new MfaCredentialInfo(c.Id, c.Type, c.Name, c.CreatedAtUtc, c.LastUsedAtUtc))
            .ToList());
}

public sealed record AuthDriverInfo(AuthDriverType Type, string DisplayName);

public sealed record TotpEnrollmentResponse(Guid CredentialId, string Secret, string OtpAuthUri);
