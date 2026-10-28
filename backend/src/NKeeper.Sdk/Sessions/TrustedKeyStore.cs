using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Sdk.Sessions;

/// <summary>Records server identities (SSH host keys, RDP certificates) the users chose to trust.</summary>
public sealed class TrustedKeyStore(NKeeperDbContext db, TimeProvider timeProvider, ILogger<TrustedKeyStore> logger)
{
    public Task TrustAsync(SessionTarget target, ServiceProtocol protocol, string algorithm, string fingerprint, Guid userId) =>
        TrustAsync(target.HostId, target.Port, protocol, algorithm, fingerprint, userId);

    public async Task TrustAsync(Guid hostId, int port, ServiceProtocol protocol, string algorithm, string fingerprint, Guid userId)
    {
        db.HostTrustedKeys.Add(new HostTrustedKey
        {
            HostId = hostId,
            Protocol = protocol,
            Port = port,
            Algorithm = algorithm,
            Fingerprint = fingerprint,
            TrustedAtUtc = timeProvider.GetUtcNow(),
            TrustedByUserId = userId,
        });
        try
        {
            // Not cancellable: the user already decided, and a half-recorded decision helps no one.
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation(
                "User {UserId} trusted {Protocol} key {Algorithm} {Fingerprint} for host {HostId} port {Port}",
                userId, protocol, algorithm, fingerprint, hostId, port);
        }
        catch (DbUpdateException)
        {
            // Another session trusted the same key at the same moment, or the host was deleted.
            db.ChangeTracker.Clear();
        }
    }

    public void LogChanged(SessionTarget target, ServiceProtocol protocol, string trusted, string presented) =>
        LogChanged(target.HostId, target.Port, protocol, trusted, presented);

    public void LogChanged(Guid hostId, int port, ServiceProtocol protocol, string trusted, string presented) =>
        logger.LogWarning(
            "{Protocol} identity of host {HostId} port {Port} changed: trusted {Trusted}, presented {Presented}",
            protocol, hostId, port, trusted, presented);

    /// <summary>The user-facing explanation of a changed identity.</summary>
    public static string ChangedMessage(string what, SessionTarget target) => ChangedMessage(what, target.Address, target.Port);

    public static string ChangedMessage(string what, string address, int port) =>
        $"The {what} of {address}:{port} has changed since it was trusted. "
        + "This happens when a server is reinstalled or its certificate renewed, but it can also mean someone is intercepting the connection. "
        + "If the change is expected, forget the trusted keys in the host's details and connect again.";
}
