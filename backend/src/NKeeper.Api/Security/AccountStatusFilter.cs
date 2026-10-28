using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Security;

/// <summary>Marks endpoints a user may call while they still have to choose a new password.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWhilePasswordChangeRequiredAttribute : Attribute;

/// <summary>
/// Re-checks the account on every authenticated request, so changes apply immediately rather
/// than at the next sign-in: disabled or deleted accounts are signed out (401), and accounts
/// that must change their password can only do that (403 "password_change_required").
/// </summary>
internal sealed class AccountStatusFilter(
    NKeeperDbContext db,
    SignInManager<ApplicationUser> signInManager,
    ProblemDetailsFactory problemDetailsFactory) : IAsyncAuthorizationFilter
{
    public const string PasswordChangeRequiredCode = "password_change_required";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (context.HttpContext.User.Identity?.IsAuthenticated != true || endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return;
        }

        var userId = context.HttpContext.User.GetUserId();
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsDisabled, u.MustChangePassword })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (account is null || account.IsDisabled)
        {
            await signInManager.SignOutAsync();
            context.Result = new UnauthorizedResult();
            return;
        }

        if (account.MustChangePassword && endpoint?.Metadata.GetMetadata<AllowWhilePasswordChangeRequiredAttribute>() is null)
        {
            var problem = problemDetailsFactory.CreateProblemDetails(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                title: "Password change required",
                detail: "Choose a new password before continuing.");
            problem.Extensions["code"] = PasswordChangeRequiredCode;
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
