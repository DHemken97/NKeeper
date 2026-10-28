using System.Security.Claims;

namespace NKeeper.Sdk.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The stable user ID of an authenticated principal.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The current principal has no user identifier.");
}
