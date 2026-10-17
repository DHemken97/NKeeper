using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Contracts.Auth;

public sealed record LoginRequest
{
    [Required(ErrorMessage = "Enter your username or email.")]
    [StringLength(256)]
    public string UserNameOrEmail { get; init; } = string.Empty;

    // Upper bound keeps attackers from forcing expensive hashing of huge inputs.
    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(512)]
    public string Password { get; init; } = string.Empty;

    public bool RememberMe { get; init; }
}

/// <summary>The safe, public view of the signed-in user. Never add Identity internals here.</summary>
/// <param name="IsDirectoryAccount">An Active Directory account: its password is changed in Windows, not NKeeper.</param>
/// <param name="SignInAccount">The domain login (CORP\jdoe) this session can pass on to RDP sessions, or null when it has none.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool MustChangePassword,
    bool IsDirectoryAccount,
    string? SignInAccount);

public sealed record AntiforgeryTokenResponse(string Token, string HeaderName);
