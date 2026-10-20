using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace NKeeper.Api.Security;

internal static class CurrentSession
{
    /// <summary>
    /// The server-side session key of this request. The cookie handler keeps it inside the
    /// encrypted cookie (not on the principal), so read it the way the handler does.
    /// </summary>
    public static string? Key(HttpContext context)
    {
        const string sessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var cookie = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        return cookie is null ? null : options.TicketDataFormat.Unprotect(cookie)?.Principal.FindFirst(sessionIdClaim)?.Value;
    }
}
