namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// Sign-in with an OpenID Connect identity provider: Microsoft Entra ID, Okta, Google, Keycloak,
/// Amazon Cognito and the like. Users who sign in this way get an NKeeper account automatically,
/// tied to the provider's id for them (not their name or email, which can change hands).
/// </summary>
public sealed class SingleSignOnOptions
{
    public const string SectionName = "SingleSignOn";

    public bool Enabled { get; set; }

    /// <summary>Shown on the sign-in page as "Sign in with {DisplayName}".</summary>
    public string DisplayName { get; set; } = "single sign-on";

    /// <summary>The provider's issuer, e.g. https://login.microsoftonline.com/{tenant-id}/v2.0. Its discovery document is read from there.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Optional: without one, NKeeper signs in as a public client (PKCE only).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Requested in addition to openid, profile and email.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// The claim that identifies a person for good. "sub" is the standard; with Entra ID, "oid"
    /// stays the same if the app registration is ever recreated.
    /// </summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>The NKeeper username of a new account (Entra ID: the UPN). Falls back to the email.</summary>
    public string UserNameClaim { get; set; } = "preferred_username";

    /// <summary>Kept up to date at every sign-in. Falls back to the username when that is an address.</summary>
    public string EmailClaim { get; set; } = "email";

    /// <summary>The claim listing the user's groups; "roles" for Entra ID app roles.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>A user must be in one of these groups (ids or names, as the provider sends them) to sign in. Anyone when empty.</summary>
    public string[] RequiredGroups { get; set; } = [];

    /// <summary>
    /// Members of any of these groups are NKeeper administrators, checked at every sign-in. When
    /// empty, administrators assign the role by hand.
    /// </summary>
    public string[] AdministratorGroups { get; set; } = [];

    internal bool IsValid() =>
        !Enabled
        || (Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            && authority.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(DisplayName)
            && !string.IsNullOrWhiteSpace(SubjectClaim)
            && !string.IsNullOrWhiteSpace(UserNameClaim)
            && !string.IsNullOrWhiteSpace(EmailClaim)
            && !string.IsNullOrWhiteSpace(GroupsClaim));
}
