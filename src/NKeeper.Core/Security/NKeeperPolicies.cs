namespace NKeeper.Core.Security;

/// <summary>Role names carried in session tokens.</summary>
public static class NKeeperRoles
{
    public const string Admin = "Admin";
}

/// <summary>Authorization policy names modules can use with <c>RequireAuthorization(...)</c>.</summary>
public static class NKeeperPolicies
{
    /// <summary>Caller must hold the <see cref="NKeeperRoles.Admin"/> role.</summary>
    public const string Admin = "Admin";
}
