namespace NKeeper.Server.Hosting;

public static class RateLimitPolicies
{
    /// <summary>Per-IP limit on anonymous sign-in endpoints to slow password and TOTP guessing.</summary>
    public const string Authentication = "authentication";
}
