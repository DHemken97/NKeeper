using System.ComponentModel.DataAnnotations;
using NKeeper.Infrastructure.Access;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Users;

public enum UserRole
{
    /// <summary>Sees and manages every host, and manages users.</summary>
    Administrator,

    /// <summary>Sees only the hosts granted to them.</summary>
    User,
}

public sealed record UserSummaryResponse(
    Guid Id,
    string UserName,
    string? Email,
    UserRole Role,
    bool IsDisabled,
    bool MustChangePassword,
    bool IsLockedOut,
    bool IsDirectoryAccount,
    int GrantCount,
    DateTimeOffset CreatedAtUtc);

public sealed record HostAccessGrantResponse(Guid Id, Guid? HostId, string? HostName, string? TagName, string? TagValue, HostAccessLevel Level);

public sealed record UserResponse(
    Guid Id,
    string UserName,
    string? Email,
    UserRole Role,
    bool IsDisabled,
    bool MustChangePassword,
    bool IsLockedOut,
    bool IsDirectoryAccount,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<HostAccessGrantResponse> Grants);

public sealed record CreateUserRequest
{
    [Required(ErrorMessage = "Enter a username.")]
    [StringLength(64, MinimumLength = 2, ErrorMessage = "Usernames are 2 to 64 characters.")]
    [RegularExpression(@"^[A-Za-z0-9._@-]+$", ErrorMessage = "Use letters, digits, '.', '_', '-' or '@'.")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Required and unique: users can sign in with it.</summary>
    [Required(ErrorMessage = "Enter an email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    public UserRole? Role { get; init; }

    /// <summary>An initial password. With <see cref="MustChangePassword"/>, the user replaces it at first sign-in.</summary>
    [Required(ErrorMessage = "Enter a password.")]
    [StringLength(512)]
    public string Password { get; init; } = string.Empty;

    public bool MustChangePassword { get; init; } = true;
}

public sealed record UpdateUserRequest
{
    /// <summary>Required and unique: users can sign in with it.</summary>
    [Required(ErrorMessage = "Enter an email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    public UserRole? Role { get; init; }

    public bool IsDisabled { get; init; }
}

public sealed record ResetPasswordRequest
{
    [Required(ErrorMessage = "Enter a password.")]
    [StringLength(512)]
    public string Password { get; init; } = string.Empty;

    public bool MustChangePassword { get; init; } = true;
}

/// <summary>Either a host (<see cref="HostId"/>) or a group (<see cref="TagName"/>, optionally <see cref="TagValue"/>).</summary>
public sealed record HostAccessGrantRequest : IValidatableObject
{
    public Guid? HostId { get; init; }

    [StringLength(HostTag.NameMaxLength)]
    public string? TagName { get; init; }

    [StringLength(HostTag.ValueMaxLength)]
    public string? TagValue { get; init; }

    [Required]
    public HostAccessLevel? Level { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasTag = !string.IsNullOrWhiteSpace(TagName);
        if (HostId is null == !hasTag)
        {
            yield return new ValidationResult("Choose a host or a group, not both.", [HostId is null ? "tagName" : "hostId"]);
        }

        if (HostId is not null && !string.IsNullOrWhiteSpace(TagValue))
        {
            yield return new ValidationResult("A host grant has no tag value.", ["tagValue"]);
        }
    }
}

public sealed record ReplaceGrantsRequest
{
    [MaxLength(500, ErrorMessage = "A user can have at most 500 grants.")]
    public HostAccessGrantRequest[] Grants { get; init; } = [];
}

public sealed record ChangePasswordRequest
{
    [Required(ErrorMessage = "Enter your current password.")]
    [StringLength(512)]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required(ErrorMessage = "Enter a new password.")]
    [StringLength(512)]
    public string NewPassword { get; init; } = string.Empty;
}
