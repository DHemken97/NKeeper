using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Infrastructure.Identity;

/// <summary>The signed-in user's domain login, for passing on to a remote session.</summary>
public sealed record SignInCredential(string UserName, string Domain, string Password)
{
    public string Display => $"{Domain}\\{UserName}";
}

/// <summary>
/// The domain password a user signed in with, kept encrypted on their server-side session
/// (<see cref="AuthSession.ProtectedSignInPassword"/>) and deleted with it at sign-out, expiry or
/// revocation. It never reaches the cookie or the browser.
/// </summary>
public sealed class SignInCredentialStore(
    NKeeperDbContext db,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<ActiveDirectoryOptions> options)
{
    private const string Purpose = "NKeeper.Identity.SignInCredentials.v1";

    /// <summary>
    /// Sign-in sets this authentication property; <see cref="DatabaseTicketStore"/> moves it out
    /// of the ticket and onto the session row.
    /// </summary>
    public const string PasswordProperty = ".nkeeper.signInPassword";

    /// <summary>Asks the session store to keep <paramref name="password"/> with the session being signed in.</summary>
    public static void Attach(AuthenticationProperties properties, string password) => properties.Items[PasswordProperty] = password;

    /// <summary>Removes the password from the ticket's properties and encrypts it, or null when there is none.</summary>
    internal static byte[]? Detach(AuthenticationProperties properties, IDataProtectionProvider dataProtectionProvider)
    {
        if (!properties.Items.Remove(PasswordProperty, out var password) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return dataProtectionProvider.CreateProtector(Purpose).Protect(Encoding.UTF8.GetBytes(password));
    }

    /// <param name="sessionKey">The server-side session of the request.</param>
    /// <returns>Null when the session is someone else's, or holds no domain password.</returns>
    public async Task<SignInCredential?> GetAsync(string? sessionKey, Guid userId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.PassCredentialsToRemoteSessions || !Guid.TryParseExact(sessionKey, "N", out var sessionId))
        {
            return null;
        }

        var row = await db.AuthSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId && s.ProtectedSignInPassword != null)
            .Join(db.Users.Where(u => u.IsDirectoryAccount), s => s.UserId, u => u.Id, (s, u) => new { u.UserName, s.ProtectedSignInPassword })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        try
        {
            var password = Encoding.UTF8.GetString(dataProtectionProvider.CreateProtector(Purpose).Unprotect(row.ProtectedSignInPassword!));
            return new SignInCredential(row.UserName!, settings.Domain!, password);
        }
        catch (CryptographicException)
        {
            // Data protection keys were removed: the user has to sign in again.
            return null;
        }
    }

    /// <summary>The account <see cref="GetAsync"/> would pass on, without decrypting the password.</summary>
    public async Task<string?> DescribeAsync(string? sessionKey, Guid userId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.PassCredentialsToRemoteSessions || !Guid.TryParseExact(sessionKey, "N", out var sessionId))
        {
            return null;
        }

        var userName = await db.AuthSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId && s.ProtectedSignInPassword != null)
            .Join(db.Users.Where(u => u.IsDirectoryAccount), s => s.UserId, u => u.Id, (s, u) => u.UserName)
            .FirstOrDefaultAsync(cancellationToken);
        return userName is null ? null : $"{settings.Domain}\\{userName}";
    }
}
