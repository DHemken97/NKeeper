using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using NKeeper.Api.Transfer;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Security;

/// <summary>
/// After an export with "lock", changes to hosts and users (anything not GET) answer 409
/// "instance_locked", so nothing is changed here that the new server would never get. Connecting
/// to hosts still works.
/// </summary>
internal sealed class InstanceLockFilter(InstanceLock instanceLock, ProblemDetailsFactory problemDetailsFactory) : IAsyncActionFilter
{
    public const string LockedCode = "instance_locked";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || context.ActionDescriptor.EndpointMetadata.OfType<BlockedWhileInstanceLockedAttribute>().FirstOrDefault() is null
            || !await instanceLock.IsLockedAsync(context.HttpContext.RequestAborted))
        {
            await next();
            return;
        }

        var problem = problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            StatusCodes.Status409Conflict,
            detail: "This instance is locked because its data was exported to move it to a new server. Make changes on the new server, or unlock it under Move to a new server.");
        problem.Extensions["code"] = LockedCode;
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }
}
