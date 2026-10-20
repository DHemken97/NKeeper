using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Contracts.Auth;
using NKeeper.Api.Security;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    CredentialValidator credentialValidator,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IAntiforgery antiforgery,
    IOptions<AntiforgeryOptions> antiforgeryOptions,
    IOptions<AuthenticationSettings> authenticationSettings,
    IOptions<ActiveDirectoryOptions> directoryOptions,
    IOptions<SingleSignOnOptions> singleSignOnOptions,
    SignInCredentialStore signInCredentials,
    Auditing.AuditLog audit,
    TimeProvider timeProvider) : ControllerBase
{
    public const string InvalidCredentialsCode = "invalid_credentials";

    /// <summary>Issues an antiforgery token. Send it as the X-XSRF-TOKEN header on POST requests.</summary>
    /// <remarks>Tokens are bound to the current user, so fetch a new one after signing in or out.</remarks>
    [HttpGet("antiforgery")]
    [AllowAnonymous]
    [ProducesResponseType<AntiforgeryTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<AntiforgeryTokenResponse> GetAntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.CacheControl = "no-store";
        return new AntiforgeryTokenResponse(tokens.RequestToken!, antiforgeryOptions.Value.HeaderName!);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitSettings.PolicyName)]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CurrentUserResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await credentialValidator.ValidateAsync(request.UserNameOrEmail, request.Password, cancellationToken);
        if (user is null)
        {
            // The name as typed (never the password), so repeated guessing shows up in the trail.
            var attempted = request.UserNameOrEmail.Trim();
            await audit.RecordAsync(new Auditing.AuditEntry(
                Auditing.AuditActions.SignInFailed, UserName: attempted.Length > 256 ? attempted[..256] : attempted));
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext,
                StatusCodes.Status401Unauthorized,
                title: "Sign-in failed",
                detail: "The username or password is incorrect.");
            problem.Extensions["code"] = InvalidCredentialsCode;
            return Unauthorized(problem);
        }

        var settings = authenticationSettings.Value;
        var lifetime = request.RememberMe ? settings.RememberMeDuration : settings.SessionTimeout;
        var properties = new AuthenticationProperties
        {
            // Non-persistent cookies are dropped when the browser closes; the server-side expiry applies either way.
            IsPersistent = request.RememberMe,
            ExpiresUtc = timeProvider.GetUtcNow().Add(lifetime),
            AllowRefresh = true,
        };

        // The session store keeps a directory account's password, encrypted, for RDP sessions.
        var directory = directoryOptions.Value;
        var passOn = user.IsDirectoryAccount && directory.Enabled && directory.PassCredentialsToRemoteSessions;
        if (passOn)
        {
            SignInCredentialStore.Attach(properties, request.Password);
        }

        await signInManager.SignInAsync(user, properties);
        await audit.RecordAsync(new Auditing.AuditEntry(
            Auditing.AuditActions.SignIn, Detail: user.IsDirectoryAccount ? "Active Directory" : null, UserId: user.Id, UserName: user.UserName));
        return await ToResponseAsync(user, passOn ? $"{directory.Domain}\\{user.UserName}" : null);
    }

    /// <summary>The ways to sign in besides a password, for the sign-in page.</summary>
    [HttpGet("options")]
    [AllowAnonymous]
    [ProducesResponseType<SignInOptionsResponse>(StatusCodes.Status200OK)]
    public SignInOptionsResponse GetSignInOptions()
    {
        var sso = singleSignOnOptions.Value;
        return new SignInOptionsResponse(sso.Enabled ? sso.DisplayName : null);
    }

    /// <summary>
    /// Starts single sign-on: a browser navigation (not a fetch) that redirects to the identity
    /// provider. Its answer comes back to /api/auth/sso/callback, which signs in and redirects to
    /// <paramref name="returnUrl"/>, or to /login?sso=failed|refused.
    /// </summary>
    [HttpGet("sso/login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitSettings.PolicyName)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SingleSignOnLogin(string? returnUrl, bool rememberMe = false)
    {
        if (!singleSignOnOptions.Value.Enabled)
        {
            return NotFound();
        }

        var properties = new AuthenticationProperties();
        properties.Items[SingleSignOn.ReturnUrlKey] = SingleSignOn.SafeReturnUrl(returnUrl);
        properties.Items[SingleSignOn.RememberMeKey] = rememberMe ? "true" : "false";
        return Challenge(properties, SingleSignOn.Scheme);
    }

    /// <summary>Ends the current session. Idempotent: succeeds even when not signed in.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            await audit.RecordAsync(new Auditing.AuditEntry(Auditing.AuditActions.SignOut));
        }

        // Removes the server-side session row via the ticket store and clears the cookie.
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserResponse>> Me()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            // The account was deleted while the session was still active.
            await signInManager.SignOutAsync();
            return Unauthorized();
        }

        Response.Headers.CacheControl = "no-store";
        return await ToResponseAsync(user, await signInCredentials.DescribeAsync(CurrentSession.Key(HttpContext), user.Id, HttpContext.RequestAborted));
    }

    private async Task<CurrentUserResponse> ToResponseAsync(ApplicationUser user, string? signInAccount)
    {
        var roles = await userManager.GetRolesAsync(user);
        return new CurrentUserResponse(
            user.Id,
            user.UserName!,
            user.Email,
            [.. roles.Order(StringComparer.Ordinal)],
            user.MustChangePassword,
            user.IsDirectoryAccount,
            user.IsSingleSignOnAccount,
            signInAccount,
            user.WorkspaceGroupBy,
            user.WorkspaceSort);
    }
}
