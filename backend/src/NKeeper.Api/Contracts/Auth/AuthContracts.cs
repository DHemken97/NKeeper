using System.ComponentModel.DataAnnotations;
using System.Text.Json;

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
/// <param name="IsSingleSignOnAccount">Signs in with the identity provider: it has no NKeeper password.</param>
/// <param name="SignInAccount">The domain login (CORP\jdoe) this session can pass on to RDP sessions, or null when it has none.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool MustChangePassword,
    bool IsDirectoryAccount,
    bool IsSingleSignOnAccount,
    string? SignInAccount,
    IReadOnlyList<string> WorkspaceGroupBy,
    string WorkspaceSort,
    string ColorScheme,
    IReadOnlyList<CustomColorScheme> CustomColorSchemes);

public sealed record WorkspacePreferencesResponse(IReadOnlyList<string> GroupBy, string Sort);

public sealed record WorkspacePreferencesRequest : IValidatableObject
{
    [MaxLength(3)]
    public string[] GroupBy { get; init; } = [];

    [Required]
    [RegularExpression("^(name|address|updated)$")]
    public string Sort { get; init; } = "name";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (GroupBy is null)
        {
            yield return new ValidationResult("Choose valid grouping keys.", [nameof(GroupBy)]);
            yield break;
        }

        if (GroupBy.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 253))
        {
            yield return new ValidationResult("Each grouping key must be between 1 and 253 characters.", [nameof(GroupBy)]);
        }

        if (GroupBy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != GroupBy.Length)
        {
            yield return new ValidationResult("Grouping keys must be unique.", [nameof(GroupBy)]);
        }
    }
}

/// <summary>A color scheme a user built: the base colors the web app derives the full palette from.</summary>
public sealed record CustomColorScheme
{
    private const string HexColor = "^#[0-9a-fA-F]{6}$";

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    [Required]
    [RegularExpression("^[a-z0-9-]{1,64}$")]
    public string Id { get; init; } = "";

    [Required(ErrorMessage = "Give the color scheme a name.")]
    [StringLength(40)]
    public string Name { get; init; } = "";

    [Required]
    [RegularExpression(HexColor)]
    public string Background { get; init; } = "";

    [Required]
    [RegularExpression(HexColor)]
    public string Surface { get; init; } = "";

    [Required]
    [RegularExpression(HexColor)]
    public string Text { get; init; } = "";

    [Required]
    [RegularExpression(HexColor)]
    public string Accent { get; init; } = "";

    public static string Serialize(IReadOnlyList<CustomColorScheme> schemes) => JsonSerializer.Serialize(schemes, StoredJson);

    /// <summary>Reads the stored list. A bad value (say, from a hand-edited transfer file) must not stop the user signing in, so it reads as empty.</summary>
    public static IReadOnlyList<CustomColorScheme> Parse(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CustomColorScheme>>(stored, StoredJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record ColorSchemesResponse(string Active, IReadOnlyList<CustomColorScheme> Custom);

public sealed record ColorSchemesRequest : IValidatableObject
{
    /// <summary>A preset's id or a custom scheme's id. Only its form is checked: the web app owns the list of presets.</summary>
    [Required]
    [RegularExpression("^[a-z0-9-]{1,64}$")]
    public string Active { get; init; } = "navy";

    [MaxLength(20, ErrorMessage = "You can keep up to 20 color schemes.")]
    public CustomColorScheme[] Custom { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Custom is null)
        {
            yield return new ValidationResult("Send a list of color schemes.", [nameof(Custom)]);
            yield break;
        }

        if (Custom.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Custom.Length)
        {
            yield return new ValidationResult("Color scheme ids must be unique.", [nameof(Custom)]);
        }
    }
}

public sealed record AntiforgeryTokenResponse(string Token, string HeaderName);

/// <summary>The password was right, and the account also needs the code just emailed (202 from login).</summary>
/// <param name="CodeSentTo">The address it went to, partly hidden (j•••@example.com).</param>
public sealed record TwoFactorChallengeResponse(bool TwoFactorRequired, string CodeSentTo);

public sealed record TwoFactorCodeRequest
{
    [Required(ErrorMessage = "Enter the code from the email.")]
    [StringLength(32)]
    public string Code { get; init; } = string.Empty;
}

/// <param name="SingleSignOnName">The identity provider's name for the "Sign in with …" button, or null without single sign-on.</param>
public sealed record SignInOptionsResponse(string? SingleSignOnName);
