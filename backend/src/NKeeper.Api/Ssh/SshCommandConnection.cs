using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Ssh;

/// <summary>The SSH host key is not trusted yet; the user must confirm it (trust on first use).</summary>
public sealed class HostKeyTrustRequiredException(string algorithm, string fingerprint) : Exception("The SSH host key must be confirmed.")
{
    public string Algorithm { get; } = algorithm;

    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// An SSH connection for running commands on a host (KVM hosts, internal scans), with the host key
/// checked against the inventory exactly like SSH sessions do (trust on first use, shared keys).
/// </summary>
internal static class SshCommandConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Connects and authenticates. An unknown host key is accepted only when it matches
    /// <paramref name="trustFingerprint"/> (then recorded as trusted); otherwise
    /// <see cref="HostKeyTrustRequiredException"/> is thrown. A changed key is refused.
    /// </summary>
    public static async Task<SshClient> ConnectAsync(
        SessionTarget target, string? trustFingerprint, Guid userId, TrustedKeyStore trustedKeys, CancellationToken cancellationToken)
    {
        if (target.Password is null && target.PrivateKey is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password or SSH key.");
        }

        var username = SshAuthentication.UserName(target);
        var methods = SshAuthentication.CreateMethods(username, target);
        var client = new SshClient(new Renci.SshNet.ConnectionInfo(target.Address, target.Port, username, [.. methods]) { Timeout = ConnectTimeout })
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        (string Algorithm, string Fingerprint)? unknown = null, newlyTrusted = null;
        string? changedFrom = null;
        client.HostKeyReceived += (_, e) =>
        {
            var known = target.KnownKeys.FirstOrDefault(k => k.Algorithm == e.HostKeyName);
            if (known is not null)
            {
                e.CanTrust = known.Fingerprint == e.FingerPrintSHA256;
                changedFrom = e.CanTrust ? null : known.Fingerprint;
            }
            else if (string.Equals(trustFingerprint, e.FingerPrintSHA256, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                newlyTrusted = (e.HostKeyName, e.FingerPrintSHA256);
            }
            else
            {
                e.CanTrust = false;
                unknown = (e.HostKeyName, e.FingerPrintSHA256);
            }
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            methods.OfType<IDisposable>().ToList().ForEach(m => m.Dispose());
            if (unknown is { } key)
            {
                throw new HostKeyTrustRequiredException(key.Algorithm, key.Fingerprint);
            }

            if (changedFrom is not null)
            {
                throw new SessionException(SessionErrorCodes.HostKeyChanged, TrustedKeyStore.ChangedMessage("SSH host key", target));
            }

            throw SshAuthentication.TranslateConnectError(ex, target, username);
        }
        finally
        {
            if (newlyTrusted is { } trusted)
            {
                await trustedKeys.TrustAsync(target, target.KeyProtocol, trusted.Algorithm, trusted.Fingerprint, userId);
            }
        }
    }
}
