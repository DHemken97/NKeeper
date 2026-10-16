using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.RemoteSessions;

/// <summary>
/// Everything needed to open a session, including decrypted secrets. Lives only in memory for
/// the duration of the connection attempt and is never serialized.
/// </summary>
internal sealed record SessionTarget(
    Guid HostId,
    string Address,
    int Port,
    HostService Service,
    string Username,
    string? Domain,
    string? Password,
    string? PrivateKey,
    string CredentialSource,
    IReadOnlyList<HostTrustedKey> KnownKeys)
{
    public string Display => $"{Username}@{Address}:{Port}";
}

/// <summary>Turns a start message into a <see cref="SessionTarget"/>, enforcing that only inventory data is used.</summary>
internal sealed class SessionTargetResolver(HostAccess access, CredentialProtector protector)
{
    /// <param name="user">Only hosts this user can see can be connected to.</param>
    /// <param name="oneOffDomain">Domain typed for one-off credentials (RDP only).</param>
    public async Task<SessionTarget> ResolveAsync(
        SessionStartMessage start, RequestUser user, ServiceProtocol protocol, int defaultPort, CancellationToken cancellationToken, string? oneOffDomain = null)
    {
        // A host the user has no access to is reported exactly like a missing one.
        var host = await access.Visible(user).AsNoTracking()
            .Include(h => h.Services)
            .Include(h => h.Credentials)
            .Include(h => h.TrustedKeys)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == start.HostId, cancellationToken)
            ?? throw new SessionException(SessionErrorCodes.HostNotFound, "This host no longer exists.");

        var service = host.Services.OrderBy(s => s.Position).ElementAtOrDefault(start.ServiceIndex);
        if (service?.Protocol != protocol)
        {
            throw new SessionException(
                SessionErrorCodes.WrongService,
                $"The selected service is not an {protocol.ToString().ToUpperInvariant()} service. Reload the host and try again.");
        }

        var port = service.Port ?? defaultPort;
        string? username, domain, password, privateKey = null;
        string credentialSource;
        if (start.CredentialId is { } credentialId)
        {
            var credential = host.Credentials.FirstOrDefault(c => c.Id == credentialId)
                ?? throw new SessionException(SessionErrorCodes.CredentialNotFound, "This credential no longer exists on the host.");
            username = credential.Username;
            domain = credential.Domain;
            password = credential.ProtectedPassword is null ? null : protector.Unprotect(credential.ProtectedPassword);
            privateKey = credential.ProtectedPrivateKey is null ? null : protector.Unprotect(credential.ProtectedPrivateKey);
            credentialSource = $"credential {credentialId}";
        }
        else
        {
            username = start.Username?.Trim();
            domain = string.IsNullOrWhiteSpace(oneOffDomain) ? null : oneOffDomain.Trim();
            password = start.Password;
            credentialSource = "one-off credentials";
        }

        if (string.IsNullOrEmpty(username))
        {
            throw new SessionException(SessionErrorCodes.MissingUsername, "This credential has no username. Edit the host to add one.");
        }

        return new SessionTarget(
            host.Id,
            host.Hostname ?? host.IpAddress!,
            port,
            service,
            username,
            domain,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(privateKey) ? null : privateKey,
            credentialSource,
            host.TrustedKeys.Where(k => k.Protocol == protocol && k.Port == port).ToList());
    }
}
