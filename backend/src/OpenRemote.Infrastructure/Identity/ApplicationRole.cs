using Microsoft.AspNetCore.Identity;

namespace OpenRemote.Infrastructure.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName)
        : base(roleName)
    {
    }
}

/// <summary>System-wide role names. Workspace-level permissions are intentionally separate.</summary>
public static class SystemRoles
{
    public const string Administrator = "Administrator";
}
