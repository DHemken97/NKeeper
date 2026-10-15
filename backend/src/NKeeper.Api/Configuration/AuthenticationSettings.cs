using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Configuration;

public sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    [Required]
    [RegularExpression("^[A-Za-z0-9._-]+$", ErrorMessage = "Authentication:CookieName may only contain letters, digits, '.', '_' and '-'.")]
    public string CookieName { get; set; } = "NKeeper.Session";

    /// <summary>Lifetime of a normal (browser-session) sign-in. Sliding: renewed while the user is active.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Lifetime of a persistent sign-in when "Remember me" is checked. Also sliding.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00")]
    public TimeSpan RememberMeDuration { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// When true, cookies are only sent over HTTPS. Must stay true in production; Development
    /// settings relax it so the API can be used over plain http://localhost.
    /// </summary>
    public bool RequireSecureCookies { get; set; } = true;
}
