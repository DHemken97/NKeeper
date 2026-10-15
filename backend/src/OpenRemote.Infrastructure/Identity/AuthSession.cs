namespace OpenRemote.Infrastructure.Identity;

/// <summary>
/// A server-side authentication session. The browser cookie only carries an encrypted
/// reference to this row, so deleting it (logout) immediately invalidates the session.
/// </summary>
public sealed class AuthSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The serialized authentication ticket, encrypted with ASP.NET Core Data Protection.</summary>
    public byte[] ProtectedTicket { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastRenewedAtUtc { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
