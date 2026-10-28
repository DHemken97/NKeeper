using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Security;

public enum TwoFactorOutcome
{
    Verified,
    WrongCode,
    /// <summary>No challenge (anymore): expired, too many wrong codes, or never started.</summary>
    Expired,
}

/// <summary>
/// The second step of signing in for accounts with "Email 2FA challenge code": a 9-digit code is
/// emailed, and the session only starts once it's entered. The pending sign-in is a
/// <see cref="TwoFactorChallenge"/> row the browser points at with an HttpOnly cookie.
/// </summary>
public sealed class TwoFactorSignIn(
    NKeeperDbContext db,
    EmailSender email,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<AuthenticationSettings> authenticationSettings,
    TimeProvider timeProvider,
    ILogger<TwoFactorSignIn> logger)
{
    public const string CookieName = "NKeeper.TwoFactor";
    public const int CodeLength = 9;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(30);

    private const string PasswordPurpose = "NKeeper.Identity.TwoFactorSignInPassword.v1";

    /// <summary>Emails a code and remembers the half-done sign-in. Returns the address, partly hidden.</summary>
    /// <exception cref="EmailException">The code couldn't be sent; nothing is kept.</exception>
    public async Task<string> StartAsync(
        HttpContext http, ApplicationUser user, bool rememberMe, string? signInMethod, string? signInPassword, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.Set<TwoFactorChallenge>().Where(c => c.ExpiresAtUtc < now || c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        var token = RandomNumberGenerator.GetBytes(32);
        var code = NewCode();
        var challenge = new TwoFactorChallenge
        {
            TokenHash = SHA256.HashData(token),
            UserId = user.Id,
            CodeHash = HashCode(token, code),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(Lifetime),
            LastSentAtUtc = now,
            RememberMe = rememberMe,
            SignInMethod = signInMethod,
            ProtectedSignInPassword = signInPassword is null
                ? null
                : dataProtectionProvider.CreateProtector(PasswordPurpose).Protect(Encoding.UTF8.GetBytes(signInPassword)),
        };

        await email.SendAsync(CodeMessage(user.Email!, code), cancellationToken);
        db.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);
        http.Response.Cookies.Append(CookieName, Base64Url(token), CookieOptions(http, challenge.ExpiresAtUtc));
        return MaskAddress(user.Email!);
    }

    /// <summary>The browser's pending sign-in and its user, or null.</summary>
    public async Task<(TwoFactorChallenge Challenge, ApplicationUser User)?> FindAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (ReadToken(http) is not { } token)
        {
            return null;
        }

        var hash = SHA256.HashData(token);
        var now = timeProvider.GetUtcNow();
        var found = await db.Set<TwoFactorChallenge>()
            .Where(c => c.TokenHash == hash && c.ExpiresAtUtc > now)
            .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new { Challenge = c, User = u })
            .FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.Challenge, found.User);
    }

    /// <summary>A fresh code for the pending sign-in; the old one stops working. Null without one.</summary>
    /// <exception cref="EmailException">The code couldn't be sent.</exception>
    /// <exception cref="TwoFactorResendTooSoonException">The last code went out moments ago.</exception>
    public async Task<string?> ResendAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (await FindAsync(http, cancellationToken) is not { } found || ReadToken(http) is not { } token)
        {
            return null;
        }

        var (challenge, user) = found;

        var now = timeProvider.GetUtcNow();
        if (now - challenge.LastSentAtUtc < ResendDelay)
        {
            throw new TwoFactorResendTooSoonException(challenge.LastSentAtUtc.Add(ResendDelay) - now);
        }

        var code = NewCode();
        await email.SendAsync(CodeMessage(user.Email!, code), cancellationToken);
        challenge.CodeHash = HashCode(token, code);
        challenge.LastSentAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return MaskAddress(user.Email!);
    }

    /// <summary>
    /// Checks the code. A right one ends the challenge (the caller signs in); wrong ones count, and
    /// the <see cref="MaxAttempts"/>th ends it too.
    /// </summary>
    public async Task<(TwoFactorOutcome Outcome, TwoFactorChallenge? Challenge, ApplicationUser? User)> VerifyAsync(
        HttpContext http, string code, CancellationToken cancellationToken)
    {
        if (await FindAsync(http, cancellationToken) is not { } found || ReadToken(http) is not { } token)
        {
            Clear(http);
            return (TwoFactorOutcome.Expired, null, null);
        }

        var (challenge, user) = found;

        var digits = new string([.. code.Where(char.IsAsciiDigit)]);
        if (digits.Length == CodeLength && CryptographicOperations.FixedTimeEquals(HashCode(token, digits), challenge.CodeHash))
        {
            db.Remove(challenge);
            await db.SaveChangesAsync(cancellationToken);
            Clear(http);
            return (TwoFactorOutcome.Verified, challenge, user);
        }

        challenge.FailedAttempts++;
        if (challenge.FailedAttempts >= MaxAttempts)
        {
            logger.LogWarning("Sign-in code for user {UserId} entered wrong {Attempts} times; the sign-in was cancelled.", user.Id, challenge.FailedAttempts);
            db.Remove(challenge);
            Clear(http);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (challenge.FailedAttempts >= MaxAttempts ? TwoFactorOutcome.Expired : TwoFactorOutcome.WrongCode, challenge, user);
    }

    /// <summary>Gives up on the pending sign-in.</summary>
    public async Task CancelAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (ReadToken(http) is { } token)
        {
            var hash = SHA256.HashData(token);
            await db.Set<TwoFactorChallenge>().Where(c => c.TokenHash == hash).ExecuteDeleteAsync(cancellationToken);
        }

        Clear(http);
    }

    public string? UnprotectPassword(TwoFactorChallenge challenge) =>
        challenge.ProtectedSignInPassword is null
            ? null
            : Encoding.UTF8.GetString(dataProtectionProvider.CreateProtector(PasswordPurpose).Unprotect(challenge.ProtectedSignInPassword));

    /// <summary>"j•••@example.com": enough to recognize, not to harvest.</summary>
    public static string MaskAddress(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 0 ? "your email address" : $"{address[0]}•••{address[at..]}";
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);

    private static byte[] HashCode(byte[] token, string code) => HMACSHA256.HashData(token, Encoding.ASCII.GetBytes(code));

    private static EmailMessage CodeMessage(string to, string code) => new(
        to,
        $"Your NKeeper sign-in code: {code}",
        $"""
        Your NKeeper sign-in code is:

            {code[..3]} {code[3..6]} {code[6..]}

        It expires in {Lifetime.TotalMinutes:0} minutes. If you didn't just try to sign in to NKeeper, someone else may know how to: tell your administrator.
        """);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? ReadToken(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || value is not { Length: 43 })
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private CookieOptions CookieOptions(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = authenticationSettings.Value.RequireSecureCookies || http.Request.IsHttps,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };

    private void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, CookieOptions(http, null));
}

public sealed class TwoFactorResendTooSoonException(TimeSpan retryAfter) : Exception("A code was sent moments ago.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
