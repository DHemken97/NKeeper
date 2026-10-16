using System.Globalization;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Kvm;

/// <summary>A virtual machine on a KVM host, as libvirt reports it.</summary>
public sealed record VirtualMachine(Guid Uuid, string Name, string State, string? Display);

/// <summary>Where a VM's SPICE console listens on the hypervisor, and its password if it has one.</summary>
public sealed record SpiceDisplay(string Host, int Port, string? Password);

/// <summary>The SSH host key is not trusted yet; the user must confirm it (trust on first use).</summary>
public sealed class HostKeyTrustRequiredException(string algorithm, string fingerprint) : Exception("The SSH host key must be confirmed.")
{
    public string Algorithm { get; } = algorithm;

    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// Talks to a KVM hypervisor the way virt-manager does: over SSH, running <c>virsh</c> against
/// <c>qemu:///system</c>. The SSH host key must be trusted (it is shared with SSH sessions).
/// </summary>
internal static partial class KvmHost
{
    private const string Virsh = "virsh -c qemu:///system";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

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
                await trustedKeys.TrustAsync(target, ServiceProtocol.Ssh, trusted.Algorithm, trusted.Fingerprint, userId);
            }
        }
    }

    /// <summary>All VMs (running or not) with their state and graphical display, if any.</summary>
    public static async Task<List<VirtualMachine>> ListAsync(SshClient client, CancellationToken cancellationToken)
    {
        // One round trip: for each VM, its UUID, name, state and display URI, tab-separated.
        const string script =
            "c='" + Virsh + "'; uuids=$($c list --all --uuid) || exit 3; for u in $uuids; do "
            + "printf '%s\\t%s\\t%s\\t%s\\n' \"$u\" \"$($c domname \"$u\")\" \"$($c domstate \"$u\")\" \"$($c domdisplay \"$u\" 2>/dev/null | head -n 1)\"; done";
        var output = await RunAsync(client, script, cancellationToken);

        var vms = new List<VirtualMachine>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 3 && Guid.TryParse(fields[0], out var uuid))
            {
                var display = fields.ElementAtOrDefault(3) is { Length: > 0 } uri ? uri.Split(':', 2)[0].ToLowerInvariant() : null;
                vms.Add(new VirtualMachine(uuid, fields[1], fields[2], display is "spice" or "vnc" ? display : null));
            }
        }

        return [.. vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The VM's SPICE address on the hypervisor (and password, when libvirt reveals it).</summary>
    public static async Task<SpiceDisplay> GetSpiceDisplayAsync(SshClient client, Guid vm, CancellationToken cancellationToken)
    {
        // The UUID is formatted by us (never user text), so it is safe in the command line.
        var id = vm.ToString("D");
        var output = await RunAsync(
            client,
            $"{Virsh} domdisplay --type spice --include-password {id} 2>/dev/null || {Virsh} domdisplay --type spice {id}",
            cancellationToken);
        return ParseSpiceUri(output.Trim().Split('\n')[0].Trim())
            ?? throw new SessionException(SessionErrorCodes.ConnectionFailed, "This VM has no SPICE display, or it isn't running.");
    }

    /// <summary>
    /// Parses libvirt's display URI, e.g. <c>spice://127.0.0.1:5900</c> or
    /// <c>spice://127.0.0.1:5900/?password=secret</c>. TLS-only SPICE ports aren't supported by the browser client.
    /// </summary>
    internal static SpiceDisplay? ParseSpiceUri(string text)
    {
        var match = SpiceUri().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var host = match.Groups["host"].Value.Trim('[', ']');
        var query = match.Groups["query"].Value;
        var password = match.Groups["userPassword"].Success ? match.Groups["userPassword"].Value : QueryValue(query, "password");
        if (!match.Groups["port"].Success)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                QueryValue(query, "tls-port") is not null
                    ? "This VM's SPICE display only accepts TLS connections, which the browser console doesn't support yet. Add a plain SPICE port (it can listen on localhost only)."
                    : "Couldn't read the VM's SPICE address.");
        }

        // A display listening on every address is reachable on the hypervisor's loopback.
        if (host is "" or "0.0.0.0" or "::")
        {
            host = "127.0.0.1";
        }

        return new SpiceDisplay(host, int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture), string.IsNullOrEmpty(password) ? null : Uri.UnescapeDataString(password));
    }

    private static string? QueryValue(string query, string name) =>
        query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name && p.Length == 2)?[1];

    private static async Task<string> RunAsync(SshClient client, string script, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(script);
        command.CommandTimeout = CommandTimeout;
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, "The hypervisor didn't answer in time.");
        }

        if (command.ExitStatus is not 0)
        {
            var error = command.Error.Trim();
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                command.ExitStatus == 127 || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? "virsh isn't installed on this host. Install the libvirt client tools (e.g. libvirt-clients or libvirt-client)."
                    : $"libvirt refused the request: {(error.Length > 0 ? error.Split('\n')[0] : $"exit code {command.ExitStatus}")}. The SSH user needs access to qemu:///system (e.g. membership in the libvirt group).");
        }

        return command.Result;
    }

    [GeneratedRegex(@"^spice://(?::(?<userPassword>[^@]*)@)?(?<host>\[[^\]]+\]|[^:/?]*)(?::(?<port>\d{1,5}))?/?(?<query>\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpiceUri();
}
