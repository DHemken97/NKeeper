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
using NKeeper.Sdk.Security;
using NKeeper.Infrastructure.Email;
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
    TwoFactorSignIn twoFactor,
    NKeeper.Sdk.Auditing.AuditLog audit,
    TimeProvider timeProvider) : ControllerBase
{
    public const string InvalidCredentialsCode = "invalid_credentials";
    public const string InvalidCodeCode = "invalid_code";
    public const string ChallengeExpiredCode = "challenge_expired";
    public const string EmailUnavailableCode = "email_unavailable";

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
    [ProducesResponseType<TwoFactorChallengeResponse>(StatusCodes.Status202Accepted)]
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
            await audit.RecordAsync(new NKeeper.Sdk.Auditing.AuditEntry(
                NKeeper.Sdk.Auditing.AuditActions.SignInFailed, UserName: attempted.Length > 256 ? attempted[..256] : attempted));
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext,
                StatusCodes.Status401Unauthorized,
                title: "Sign-in failed",
                detail: "The username or password is incorrect.");
            problem.Extensions["code"] = InvalidCredentialsCode;
            return Unauthorized(problem);
        }

        // The session store keeps a directory account's password, encrypted, for RDP sessions.
        var directory = directoryOptions.Value;
        var passOn = user.IsDirectoryAccount && directory.Enabled && directory.PassCredentialsToRemoteSessions;
        var method = user.IsDirectoryAccount ? "Active Directory" : null;
        var signInPassword = passOn ? request.Password : null;
        if (!user.TwoFactorEnabled)
        {
            return await CompleteSignInAsync(user, request.RememberMe, method, signInPassword);
        }

        try
        {
            var sentTo = await twoFactor.StartAsync(HttpContext, user, request.RememberMe, method, signInPassword, cancellationToken);
            return StatusCode(StatusCodes.Status202Accepted, new TwoFactorChallengeResponse(true, sentTo));
        }
        catch (EmailException)
        {
            await audit.RecordAsync(new NKeeper.Sdk.Auditing.AuditEntry(
                NKeeper.Sdk.Auditing.AuditActions.SignInFailed, Detail: "The sign-in code couldn't be emailed", UserId: user.Id, UserName: user.UserName));
            return CodedProblem(
                StatusCodes.Status503ServiceUnavailable,
                EmailUnavailableCode,
                "Sign-in code not sent",
                "Your account needs a code sent by email, and it couldn't be sent. Ask an administrator to check the email settings.");
        }
    }

    /// <summary>The pending sign-in waiting for its emailed code, if this browser has one.</summary>
    [HttpGet("two-factor")]
    [AllowAnonymous]
    [ProducesResponseType<TwoFactorChallengeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TwoFactorChallengeResponse>> GetTwoFactorChallenge(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await twoFactor.FindAsync(HttpContext, cancellationToken) is { } pending
            ? new TwoFactorChallengeResponse(true, TwoFactorSignIn.MaskAddress(pending.User.Email!))
            : NotFound();
    }

    /// <summary>Finishes a sign-in with the code that was emailed. Five wrong codes cancel it.</summary>
    [HttpPost("two-factor")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitSettings.PolicyName)]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CurrentUserResponse>> VerifyTwoFactor(TwoFactorCodeRequest request, CancellationToken cancellationToken)
    {
        var (outcome, challenge, user) = await twoFactor.VerifyAsync(HttpContext, request.Code, cancellationToken);
        if (user is not null && outcome != TwoFactorOutcome.Verified)
        {
            await audit.RecordAsync(new NKeeper.Sdk.Auditing.AuditEntry(
                NKeeper.Sdk.Auditing.AuditActions.SignInFailed, Detail: "Wrong sign-in code", UserId: user.Id, UserName: user.UserName));
        }

        if (outcome == TwoFactorOutcome.Verified && user is { IsDisabled: false } && challenge is not null)
        {
            return await CompleteSignInAsync(user, challenge.RememberMe, challenge.SignInMethod, twoFactor.UnprotectPassword(challenge), "emailed code");
        }

        return outcome == TwoFactorOutcome.WrongCode
            ? CodedProblem(StatusCodes.Status401Unauthorized, InvalidCodeCode, "Sign-in failed", "That code isn't right. Check the email and try again.")
            : CodedProblem(StatusCodes.Status401Unauthorized, ChallengeExpiredCode, "Sign-in failed", "This sign-in has expired. Sign in again to get a new code.");
    }

    /// <summary>Emails a new code for the pending sign-in; the previous one stops working.</summary>
    [HttpPost("two-factor/resend")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitSettings.PolicyName)]
    [ProducesResponseType<TwoFactorChallengeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TwoFactorChallengeResponse>> ResendTwoFactor(CancellationToken cancellationToken)
    {
        try
        {
            return await twoFactor.ResendAsync(HttpContext, cancellationToken) is { } sentTo
                ? new TwoFactorChallengeResponse(true, sentTo)
                : CodedProblem(StatusCodes.Status401Unauthorized, ChallengeExpiredCode, "Sign-in failed", "This sign-in has expired. Sign in again to get a new code.");
        }
        catch (TwoFactorResendTooSoonException tooSoon)
        {
            var seconds = (int)Math.Ceiling(tooSoon.RetryAfter.TotalSeconds);
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return CodedProblem(StatusCodes.Status429TooManyRequests, ApiServiceCollectionExtensions.RateLimitedCode, "Too many attempts", "A code was just sent. Wait a moment before asking for another.");
        }
        catch (EmailException)
        {
            return CodedProblem(StatusCodes.Status503ServiceUnavailable, EmailUnavailableCode, "Sign-in code not sent", "The code couldn't be sent. Try again, or ask an administrator to check the email settings.");
        }
    }

    /// <summary>Gives up on the pending sign-in, e.g. to sign in as someone else.</summary>
    [HttpPost("two-factor/cancel")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CancelTwoFactor(CancellationToken cancellationToken)
    {
        await twoFactor.CancelAsync(HttpContext, cancellationToken);
        return NoContent();
    }

    /// <summary>Starts the session of a user who passed every step.</summary>
    /// <param name="signInPassword">A domain password to keep with the session for RDP, or null.</param>
    private async Task<ActionResult<CurrentUserResponse>> CompleteSignInAsync(
        ApplicationUser user, bool rememberMe, string? method, string? signInPassword, string? secondStep = null)
    {
        var settings = authenticationSettings.Value;
        var properties = new AuthenticationProperties
        {
            // Non-persistent cookies are dropped when the browser closes; the server-side expiry applies either way.
            IsPersistent = rememberMe,
            ExpiresUtc = timeProvider.GetUtcNow().Add(rememberMe ? settings.RememberMeDuration : settings.SessionTimeout),
            AllowRefresh = true,
        };
        if (signInPassword is not null)
        {
            SignInCredentialStore.Attach(properties, signInPassword);
        }

        await signInManager.SignInAsync(user, properties);
        var detail = string.Join(" · ", new[] { method, secondStep }.Where(part => part is not null));
        await audit.RecordAsync(new NKeeper.Sdk.Auditing.AuditEntry(
            NKeeper.Sdk.Auditing.AuditActions.SignIn, Detail: detail.Length > 0 ? detail : null, UserId: user.Id, UserName: user.UserName));
        return await ToResponseAsync(user, signInPassword is not null ? $"{directoryOptions.Value.Domain}\\{user.UserName}" : null);
    }

    private ObjectResult CodedProblem(int statusCode, string code, string title, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode, title: title, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = statusCode };
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
            await audit.RecordAsync(new NKeeper.Sdk.Auditing.AuditEntry(NKeeper.Sdk.Auditing.AuditActions.SignOut));
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
