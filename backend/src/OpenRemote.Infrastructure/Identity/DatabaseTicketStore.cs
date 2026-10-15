using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenRemote.Infrastructure.Persistence;

namespace OpenRemote.Infrastructure.Identity;

/// <summary>
/// Stores cookie authentication tickets in PostgreSQL so sessions are server-managed:
/// the cookie holds only an opaque session key, and logout deletes the session row.
/// </summary>
/// <remarks>Registered as a singleton (cookie options require it); each call uses its own DbContext scope.</remarks>
public sealed class DatabaseTicketStore(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : ITicketStore
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("OpenRemote.Identity.AuthSessions.v1");

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = GetUserId(ticket),
            ProtectedTicket = Protect(ticket),
            CreatedAtUtc = now,
            LastRenewedAtUtc = now,
            ExpiresAtUtc = ticket.Properties.ExpiresUtc,
        };

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenRemoteDbContext>();

        // Opportunistically purge expired sessions so the table does not grow without bound.
        await db.AuthSessions.Where(s => s.ExpiresAtUtc < now).ExecuteDeleteAsync(cancellationToken);

        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id.ToString("N");
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return;
        }

        var protectedTicket = Protect(ticket);
        var now = timeProvider.GetUtcNow();
        var expires = ticket.Properties.ExpiresUtc;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenRemoteDbContext>();

        // Update in place only: a session revoked concurrently must not be resurrected.
        await db.AuthSessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.ProtectedTicket, protectedTicket)
                    .SetProperty(s => s.LastRenewedAtUtc, now)
                    .SetProperty(s => s.ExpiresAtUtc, expires),
                cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenRemoteDbContext>();

        var session = await db.AuthSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAtUtc is { } expires && expires <= timeProvider.GetUtcNow())
        {
            await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            // Data protection keys were removed or the row was tampered with.
            await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenRemoteDbContext>();
        await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private static bool TryParseKey(string key, out Guid id) => Guid.TryParseExact(key, "N", out id);

    private static Guid GetUserId(AuthenticationTicket ticket)
    {
        var value = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException("Authentication ticket does not contain a valid user identifier.");
    }
}

/// <summary>Attaches <see cref="DatabaseTicketStore"/> to the Identity application cookie.</summary>
internal sealed class ConfigureSessionStore(DatabaseTicketStore store) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name == IdentityConstants.ApplicationScheme)
        {
            options.SessionStore = store;
        }
    }

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);
}
