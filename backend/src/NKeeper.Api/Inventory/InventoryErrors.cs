using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using NKeeper.Sdk.Inventory;

namespace NKeeper.Api.Inventory;

/// <summary>Turns inventory domain failures into consistent problem responses.</summary>
internal sealed class InventoryExceptionFilter(ProblemDetailsFactory problemDetailsFactory) : IExceptionFilter
{
    public const string ConcurrencyConflictCode = "concurrency_conflict";

    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case InventoryValidationException validation:
                var modelState = new ModelStateDictionary();
                modelState.AddModelError(validation.Field, validation.Message);
                var validationProblem = problemDetailsFactory.CreateValidationProblemDetails(context.HttpContext, modelState);
                context.Result = new BadRequestObjectResult(validationProblem);
                context.ExceptionHandled = true;
                break;

            case NKeeper.Sdk.Access.HostAccessDeniedException denied:
                var forbidden = problemDetailsFactory.CreateProblemDetails(
                    context.HttpContext, StatusCodes.Status403Forbidden, title: "Not allowed", detail: denied.Message);
                forbidden.Extensions["code"] = "forbidden";
                context.Result = new ObjectResult(forbidden) { StatusCode = StatusCodes.Status403Forbidden };
                context.ExceptionHandled = true;
                break;

            case DbUpdateConcurrencyException:
                var conflict = problemDetailsFactory.CreateProblemDetails(
                    context.HttpContext,
                    StatusCodes.Status409Conflict,
                    title: "Changed by someone else",
                    detail: "This item was changed or deleted since you opened it. Reload to see the latest version.");
                conflict.Extensions["code"] = ConcurrencyConflictCode;
                context.Result = new ConflictObjectResult(conflict);
                context.ExceptionHandled = true;
                break;
        }
    }
}
