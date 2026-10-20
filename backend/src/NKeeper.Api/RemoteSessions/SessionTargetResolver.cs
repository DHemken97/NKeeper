using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Access;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.RemoteSessions;

/// <summary>
/// Everything needed to open a session, including decrypted secrets. Lives only in memory for
/// the duration of the connection attempt and is never serialized.
/// </summary>
/// <param name="KeyProtocol">The protocol the server's identities (host keys, certificates) are trusted under.</param>
/// <param name="Jump">The jump host to tunnel through (its own SSH target), when the host has one.</param>
/// <param name="HostName">The host's name, for messages.</param>
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
    IReadOnlyList<HostTrustedKey> KnownKeys,
    ServiceProtocol KeyProtocol,
    SessionTarget? Jump = null,
    string HostName = "")
{
    public string Display => Username.Length > 0 ? $"{Username}@{Address}:{Port}" : $"{Address}:{Port}";
}

/// <summary>Turns a start message into a <see cref="SessionTarget"/>, enforcing that only inventory data is used.</summary>
internal sealed class SessionTargetResolver(HostAccess access, CredentialProtector protector, SignInCredentialStore signInCredentials)
{
    /// <param name="user">Only hosts this user can see can be connected to.</param>
    /// <param name="oneOffDomain">Domain typed for one-off credentials (RDP only).</param>
    /// <param name="requireUsername">False for VNC, where servers usually ask for a password only.</param>
    /// <remarks>
    /// An iDRAC service can also be opened as <see cref="ServiceProtocol.Ssh"/>: that is RACADM, the
    /// iDRAC's own SSH command line, on port 22 of the iDRAC's address.
    /// </remarks>
    public async Task<SessionTarget> ResolveAsync(
        SessionStartMessage start,
        RequestUser user,
        ServiceProtocol protocol,
        int defaultPort,
        CancellationToken cancellationToken,
        string? oneOffDomain = null,
        bool requireUsername = true)
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
        var racadm = service?.Protocol == ServiceProtocol.Idrac && protocol == ServiceProtocol.Ssh;
        if (service is null || (service.Protocol != protocol && !racadm))
        {
            throw new SessionException(
                SessionErrorCodes.WrongService,
                $"The selected service is not an {protocol.ToString().ToUpperInvariant()} service. Reload the host and try again.");
        }

        // RACADM always uses the iDRAC's SSH port; the service's port is its HTTPS (Redfish) port.
        var port = racadm ? defaultPort : service.Port ?? defaultPort;
        string? username, domain, password, privateKey = null;
        string credentialSource;
        if (start.UseSignInCredentials)
        {
            // RDP only: the user's own domain login goes nowhere they didn't ask for it to.
            if (protocol != ServiceProtocol.Rdp || racadm)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "Your sign-in can only be passed on to RDP sessions.");
            }

            var signIn = await signInCredentials.GetAsync(user.SessionKey, user.Id, cancellationToken)
                ?? throw new SessionException(
                    SessionErrorCodes.SignInCredentialsUnavailable,
                    "Your NKeeper sign-in has no domain login to pass on. Sign out, then sign in with your Active Directory account.");
            username = signIn.UserName;
            domain = signIn.Domain;
            password = signIn.Password;
            credentialSource = $"the signed-in user's domain account {signIn.Display}";
        }
        else if (start.CredentialId is { } credentialId)
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

        if (string.IsNullOrEmpty(username) && requireUsername)
        {
            throw new SessionException(SessionErrorCodes.MissingUsername, "This credential has no username. Edit the host to add one.");
        }

        // A KVM host is reached over SSH: its host keys are SSH host keys, shared with SSH sessions.
        // An iDRAC's identities belong to its own address, so they're kept apart from the host's.
        var keyProtocol = service.Protocol switch
        {
            ServiceProtocol.Kvm => ServiceProtocol.Ssh,
            ServiceProtocol.Idrac => ServiceProtocol.Idrac,
            _ => protocol,
        };
        return new SessionTarget(
            host.Id,
            // The service's own address (an iDRAC's), else the host's: an IP address is used over the
            // hostname when both are set (no DNS dependency).
            service.Address ?? host.IpAddress ?? host.Hostname!,
            port,
            service,
            username ?? string.Empty,
            domain,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(privateKey) ? null : privateKey,
            credentialSource,
            host.TrustedKeys.Where(k => k.Protocol == keyProtocol && k.Port == port).ToList(),
            keyProtocol,
            host.JumpHostId is { } jumpId ? await ResolveJumpAsync(jumpId, host.JumpCredentialId, user, cancellationToken) : null,
            host.Name);
    }

    /// <summary>
    /// The SSH target of a jump host: its first SSH service, with the chosen credential (or its first
    /// with a username and a secret). The user must have access to the jump host too, so a jump can't
    /// lend out the credentials of a host they can't see.
    /// </summary>
    public async Task<SessionTarget> ResolveJumpAsync(Guid jumpHostId, Guid? credentialId, RequestUser user, CancellationToken cancellationToken)
    {
        var jump = await access.Visible(user).AsNoTracking()
            .Include(h => h.Services)
            .Include(h => h.Credentials)
            .Include(h => h.TrustedKeys)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == jumpHostId, cancellationToken)
            ?? throw new SessionException(
                SessionErrorCodes.HostNotFound,
                "This host is reached through a jump host you don't have access to. Ask an administrator for access to the jump host too.");

        var service = jump.Services.OrderBy(s => s.Position).FirstOrDefault(s => s.Protocol == ServiceProtocol.Ssh)
            ?? throw new SessionException(SessionErrorCodes.WrongService, $"The jump host “{jump.Name}” has no SSH service any more.");
        var credential = credentialId is { } id
            ? jump.Credentials.FirstOrDefault(c => c.Id == id)
            : jump.Credentials.OrderBy(c => c.Position).FirstOrDefault(c => c.Username is not null && (c.ProtectedPassword is not null || c.ProtectedPrivateKey is not null));
        if (credential?.Username is null)
        {
            throw new SessionException(SessionErrorCodes.CredentialNotFound, $"The jump host “{jump.Name}” has no credential to sign in with. Edit it to add one.");
        }

        var port = service.Port ?? 22;
        return new SessionTarget(
            jump.Id,
            jump.IpAddress ?? jump.Hostname!,
            port,
            service,
            credential.Username,
            credential.Domain,
            credential.ProtectedPassword is null ? null : protector.Unprotect(credential.ProtectedPassword),
            credential.ProtectedPrivateKey is null ? null : protector.Unprotect(credential.ProtectedPrivateKey),
            $"jump host credential {credential.Id}",
            jump.TrustedKeys.Where(k => k.Protocol == ServiceProtocol.Ssh && k.Port == port).ToList(),
            ServiceProtocol.Ssh,
            HostName: jump.Name);
    }
}
