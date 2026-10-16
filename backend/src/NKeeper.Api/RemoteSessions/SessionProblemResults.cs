using Microsoft.AspNetCore.Mvc;

namespace NKeeper.Api.RemoteSessions;

internal static class SessionProblemResults
{
    /// <summary>Session failures as problem details with the session error code (never 401, which would sign the user out).</summary>
    public static ObjectResult SessionFailure(this ControllerBase controller, SessionException ex)
    {
        var status = ex.Code switch
        {
            SessionErrorCodes.HostNotFound or SessionErrorCodes.CredentialNotFound => StatusCodes.Status404NotFound,
            SessionErrorCodes.HostKeyChanged => StatusCodes.Status409Conflict,
            SessionErrorCodes.TooManySessions => StatusCodes.Status429TooManyRequests,
            SessionErrorCodes.ConnectionFailed or SessionErrorCodes.Timeout => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(controller.HttpContext, status, detail: ex.Message);
        problem.Extensions["code"] = ex.Code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
