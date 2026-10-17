using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Contracts.Users;
using NKeeper.Api.Security;
using NKeeper.Api.Users;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Controllers;

/// <summary>User accounts and their host access. Administrators only.</summary>
[ApiController]
[BlockedWhileInstanceLocked]
[Route("api/users")]
[Authorize(Roles = SystemRoles.Administrator)]
public sealed class UsersController(UserAdministration users) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<UserSummaryResponse>>(StatusCodes.Status200OK)]
    public Task<List<UserSummaryResponse>> List(CancellationToken cancellationToken) => users.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await users.GetAsync(id, cancellationToken) is { } user ? user : NotFound();

    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await users.CreateAsync(request, User.GetUserId(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user);
    }

    /// <summary>Changes email, role and disabled state. Demoting or disabling ends the user's sessions.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        await users.UpdateAsync(id, request, User.GetUserId(), cancellationToken) is { } user ? user : NotFound();

    /// <summary>Sets a new password (unlocking the account) and ends the user's sessions.</summary>
    [HttpPost("{id:guid}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken) =>
        await users.ResetPasswordAsync(id, request, User.GetUserId(), cancellationToken) ? NoContent() : NotFound();

    /// <summary>Replaces the user's host access grants.</summary>
    [HttpPut("{id:guid}/grants")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> ReplaceGrants(Guid id, ReplaceGrantsRequest request, CancellationToken cancellationToken) =>
        await users.ReplaceGrantsAsync(id, request, User.GetUserId(), cancellationToken) is { } user ? user : NotFound();

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await users.DeleteAsync(id, User.GetUserId(), cancellationToken) ? NoContent() : NotFound();
}

/// <summary>The signed-in user's own account.</summary>
[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountController(
    UserManager<ApplicationUser> userManager,
    SessionRevoker sessions,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions) : ControllerBase
{
    /// <summary>
    /// Changes the caller's password. Other sessions of the account are signed out; this one stays.
    /// Rate limited like sign-in, since it verifies the current password.
    /// </summary>
    [HttpPost("password")]
    [AllowWhilePasswordChangeRequired]
    [EnableRateLimiting(LoginRateLimitSettings.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        if (request.CurrentPassword == request.NewPassword)
        {
            throw new Inventory.InventoryValidationException("newPassword", "Choose a password different from your current one.");
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        UserAdministration.ThrowOnFailure(result, defaultField: "newPassword");

        user.MustChangePassword = false;
        UserAdministration.ThrowOnFailure(await userManager.UpdateAsync(user), defaultField: "newPassword");
        // Sign out the account's other sessions; this one continues.
        await sessions.RevokeAllAsync(user.Id, cancellationToken, exceptSessionKey: CurrentSessionKey());
        return NoContent();
    }

    /// <summary>
    /// The server-side session key of this request. The cookie handler keeps it inside the
    /// encrypted cookie (not on the principal), so read it the way the handler does.
    /// </summary>
    private string? CurrentSessionKey()
    {
        const string sessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";
        var options = cookieOptions.Get(IdentityConstants.ApplicationScheme);
        var cookie = options.CookieManager.GetRequestCookie(HttpContext, options.Cookie.Name!);
        return cookie is null ? null : options.TicketDataFormat.Unprotect(cookie)?.Principal.FindFirst(sessionIdClaim)?.Value;
    }
}
