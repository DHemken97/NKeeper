using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Security;

/// <summary>
/// The system administrator: the instance's first administrator account, the one setup creates (an instance
/// export keeps its creation date, so it stays the first). Other administrators are ordinary administrators.
/// Some pages are for the system administrator alone, e.g. the insurance questionnaire.
/// </summary>
internal static class SystemAdministrator
{
    public static async Task<bool> IsAsync(UserManager<ApplicationUser> users, Guid userId)
    {
        var administrators = await users.GetUsersInRoleAsync(SystemRoles.Administrator);
        return administrators.OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id).FirstOrDefault()?.Id == userId;
    }
}

/// <summary>Only the system administrator may use this; to anyone else it doesn't exist (404).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SystemAdministratorOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return; // [Authorize] answers 401.
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await SystemAdministrator.IsAsync(users, context.HttpContext.User.GetUserId()))
        {
            context.Result = new NotFoundResult();
        }
    }
}
