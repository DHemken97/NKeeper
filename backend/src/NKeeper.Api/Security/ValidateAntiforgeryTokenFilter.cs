using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Security;

/// <summary>
/// Requires a valid antiforgery token (cookie + <c>X-XSRF-TOKEN</c> header) on every unsafe request.
/// Unlike the built-in filter it returns a problem response with a stable <c>code</c>, so the
/// frontend can refresh its token and retry once.
/// </summary>
internal sealed class ValidateAntiforgeryTokenFilter(IAntiforgery antiforgery, ProblemDetailsFactory problemDetailsFactory)
    : IAsyncAuthorizationFilter
{
    public const string ErrorCode = "csrf_token_invalid";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return;
        }

        if (await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return;
        }

        var problem = problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            StatusCodes.Status400BadRequest,
            title: "Invalid security token",
            detail: "The request security token is missing or expired. Refresh the page and try again.");
        problem.Extensions["code"] = ErrorCode;
        context.Result = new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
