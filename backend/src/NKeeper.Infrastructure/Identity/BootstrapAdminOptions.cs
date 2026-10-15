namespace NKeeper.Infrastructure.Identity;

/// <summary>
/// Development-time creation of an initial administrator. Disabled unless explicitly enabled,
/// and refused outside the Development environment unless <see cref="AllowOutsideDevelopment"/> is set.
/// </summary>
public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";

    public bool Enabled { get; set; }

    public bool AllowOutsideDevelopment { get; set; }

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    internal bool IsValid() =>
        !Enabled
        || (!string.IsNullOrWhiteSpace(UserName)
            && !string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(Password));
}
