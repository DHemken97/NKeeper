using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Data;
using NKeeper.Infrastructure.Options;
using NKeeper.Infrastructure.Services;
using NKeeper.Server.Hosting;

namespace NKeeper.Server.Controllers;

/// <summary>
/// Two-stage sign-in: primary credentials via a pluggable driver, then (if enrolled) a TOTP code.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IEnumerable<IAuthenticationDriver> drivers,
    UserProvisioningService provisioning,
    AppDbContext db,
    JwtTokenService tokens,
    TotpService totp,
    IDataProtectionProvider dataProtection,
    IOptions<AuthenticationOptions> authOptions,
    TimeProvider timeProvider,
    ILogger<AuthController> logger) : ControllerBase
{
    private const string TotpProtectorPurpose = "NKeeper.Mfa.Totp.v1";

    private IDataProtector TotpProtector => dataProtection.CreateProtector(TotpProtectorPurpose);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Identity sources the login form should offer.</summary>
    [HttpGet("drivers")]
    [AllowAnonymous]
    public IEnumerable<AuthDriverInfo> GetDrivers() =>
        drivers
            .Where(d => d.IsEnabled)
            .Select(d => new AuthDriverInfo(d.DriverType, DescribeDriver(d.DriverType)));

    /// <summary>Stage 1: primary credentials. Returns a session token, or an mfa_pending token if a second factor is enrolled.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var driver = drivers.FirstOrDefault(d => d.DriverType == request.Driver && d.IsEnabled);
        if (driver is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "That sign-in method is not available.");
        }

        var result = await driver.AuthenticateAsync(request.Username, request.Password, ct);
        if (!result.Succeeded)
        {
            logger.LogWarning("Sign-in failed for {Username} via {Driver}: {Reason}",
                request.Username, request.Driver, result.FailureReason);
            return InvalidCredentials();
        }

        var user = await provisioning.ResolveUserAsync(result, ct);
        if (user is null)
        {
            logger.LogWarning("Sign-in for {Username} via {Driver} succeeded but no NKeeper account exists and auto-provisioning is off",
                result.Username, result.Driver);
            return InvalidCredentials();
        }

        if (!CanSignIn(user))
        {
            logger.LogWarning("Sign-in blocked for {Username}: account disabled or locked out", user.Username);
            return InvalidCredentials();
        }

        var hasTotp = user.MfaCredentials.Any(c => c.IsConfirmed && c.Type == MfaCredentialType.Totp);
        if (hasTotp)
        {
            var pending = tokens.CreateMfaPendingToken(user, result.Driver);
            return Ok(new LoginResponse(
                LoginStatus.MfaRequired,
                ExpiresAtUtc: pending.ExpiresAtUtc,
                MfaToken: pending.Token,
                MfaMethods: ["totp"]));
        }

        return Ok(await CompleteSignInAsync(user, ["pwd"], ct));
    }

    /// <summary>Stage 2: exchange an mfa_pending token and a TOTP code for a session token.</summary>
    [HttpPost("mfa/verify-totp")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<LoginResponse>> VerifyTotp(VerifyTotpRequest request, CancellationToken ct)
    {
        var pending = await tokens.ValidateMfaPendingTokenAsync(request.MfaToken);
        if (pending is null)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Your sign-in session expired. Please sign in again.");
        }

        var user = await db.Users
            .Include(u => u.MfaCredentials)
            .SingleOrDefaultAsync(u => u.Id == pending.UserId, ct);

        if (user is null || !CanSignIn(user))
        {
            return InvalidCode();
        }

        var protector = TotpProtector;
        foreach (var credential in user.MfaCredentials.Where(c => c.IsConfirmed && c.Type == MfaCredentialType.Totp))
        {
            var secret = protector.Unprotect(credential.ProtectedSecret!);
            if (!totp.ValidateCode(secret, request.Code, credential.LastUsedTimeStep, out var matchedStep))
            {
                continue;
            }

            credential.LastUsedTimeStep = matchedStep;
            credential.LastUsedAtUtc = UtcNow;

            try
            {
                return Ok(await CompleteSignInAsync(user, ["pwd", "otp", "mfa"], ct));
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request consumed this code first.
                return InvalidCode();
            }
        }

        RegisterFailedAttempt(user);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Invalid TOTP code for {Username}", user.Username);
        return InvalidCode();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserInfo>> Me(CancellationToken ct)
    {
        var user = await LoadCurrentUserAsync(ct);
        return user is null ? Unauthorized() : Ok(UserInfo.From(user));
    }

    /// <summary>Starts TOTP enrollment. The credential stays inactive until confirmed with a valid code.</summary>
    [HttpPost("mfa/totp/enroll")]
    [Authorize]
    public async Task<ActionResult<TotpEnrollmentResponse>> BeginTotpEnrollment(BeginTotpEnrollmentRequest request, CancellationToken ct)
    {
        var user = await LoadCurrentUserAsync(ct);
        if (user is null)
        {
            return Unauthorized();
        }

        // Only one enrollment in flight at a time.
        var stale = user.MfaCredentials.Where(c => c.Type == MfaCredentialType.Totp && !c.IsConfirmed).ToList();
        db.MfaCredentials.RemoveRange(stale);

        var secret = totp.GenerateSecret();
        var credential = new MfaCredential
        {
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Authenticator app" : request.Name.Trim(),
            ProtectedSecret = TotpProtector.Protect(secret),
            IsConfirmed = false
        };
        db.MfaCredentials.Add(credential);
        await db.SaveChangesAsync(ct);

        var uri = TotpService.GetQrCodeUri(authOptions.Value.TotpIssuer, user.Username, secret);
        return Ok(new TotpEnrollmentResponse(credential.Id, secret, uri));
    }

    [HttpPost("mfa/totp/confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmTotpEnrollment(ConfirmTotpRequest request, CancellationToken ct)
    {
        var userId = JwtTokenService.GetUserId(User);
        var credential = await db.MfaCredentials.SingleOrDefaultAsync(
            c => c.Id == request.CredentialId && c.UserId == userId && c.Type == MfaCredentialType.Totp && !c.IsConfirmed, ct);

        if (credential is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Enrollment not found. Start again.");
        }

        var secret = TotpProtector.Unprotect(credential.ProtectedSecret!);
        if (!totp.ValidateCode(secret, request.Code, null, out var matchedStep))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "That code didn't match. Check your device's clock and try again.");
        }

        credential.IsConfirmed = true;
        credential.LastUsedTimeStep = matchedStep;
        credential.LastUsedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} enrolled TOTP credential {CredentialId}", userId, credential.Id);
        return NoContent();
    }

    private async Task<LoginResponse> CompleteSignInAsync(User user, string[] authenticationMethods, CancellationToken ct)
    {
        user.LastLoginAtUtc = UtcNow;
        user.AccessFailedCount = 0;
        await db.SaveChangesAsync(ct);

        var session = tokens.CreateSessionToken(user, authenticationMethods);
        logger.LogInformation("User {Username} signed in via {AuthSource} ({Methods})",
            user.Username, user.AuthSource, string.Join('+', authenticationMethods));

        return new LoginResponse(LoginStatus.Authenticated, session.Token, session.ExpiresAtUtc, UserInfo.From(user));
    }

    private async Task<User?> LoadCurrentUserAsync(CancellationToken ct)
    {
        var userId = JwtTokenService.GetUserId(User);
        if (userId is null)
        {
            return null;
        }

        return await db.Users
            .Include(u => u.MfaCredentials)
            .SingleOrDefaultAsync(u => u.Id == userId && u.IsEnabled, ct);
    }

    private bool CanSignIn(User user) =>
        user.IsEnabled && (user.LockoutEndUtc is null || user.LockoutEndUtc <= UtcNow);

    private void RegisterFailedAttempt(User user)
    {
        user.AccessFailedCount++;
        if (user.AccessFailedCount >= authOptions.Value.MaxFailedAttempts)
        {
            user.LockoutEndUtc = UtcNow.AddMinutes(authOptions.Value.LockoutMinutes);
            user.AccessFailedCount = 0;
        }
    }

    // Deliberately vague: don't reveal whether the username, password, or account state was the problem.
    private ObjectResult InvalidCredentials() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password.");

    private ObjectResult InvalidCode() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid verification code.");

    private static string DescribeDriver(AuthDriverType type) => type switch
    {
        AuthDriverType.LocalDatabase => "NKeeper account",
        AuthDriverType.ActiveDirectory => "Active Directory",
        AuthDriverType.LinuxPam => "Linux (PAM)",
        AuthDriverType.OpenIdConnect => "Single sign-on",
        _ => type.ToString()
    };
}
