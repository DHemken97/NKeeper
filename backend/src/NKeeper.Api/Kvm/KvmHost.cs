using System.Globalization;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Kvm;

/// <summary>A virtual machine on a KVM host, as libvirt or Proxmox reports it.</summary>
/// <param name="Id">libvirt's UUID, or the Proxmox VM ID.</param>
/// <param name="State">libvirt's state ("running", "shut off"…) or Proxmox's ("running", "stopped"…).</param>
/// <param name="Display">The console protocol (one of <see cref="ConsoleProtocols"/>), or null for none.</param>
/// <param name="Node">The Proxmox node the VM is on; null for libvirt (the host itself).</param>
public sealed record VirtualMachine(string Id, string Name, string State, string? Display, string? Node = null);

/// <summary>A hypervisor's VMs, and whether it is a Proxmox node (and which one).</summary>
internal sealed record HypervisorListing(IReadOnlyList<VirtualMachine> Vms, string? ProxmoxNode);

/// <summary>The console protocols NKeeper opens for VMs (libvirt's display URI schemes).</summary>
public static class ConsoleProtocols
{
    public const string Spice = "spice";
    public const string Vnc = "vnc";

    public static string DisplayName(string protocol) => protocol.ToUpperInvariant();
}

/// <summary>Where a VM's console (SPICE or VNC) listens on the hypervisor, and its password if it has one.</summary>
public sealed record ConsoleDisplay(string Protocol, string Host, int Port, string? Password);

/// <summary>
/// Talks to a KVM hypervisor the way virt-manager does: over SSH, running <c>virsh</c> against
/// <c>qemu:///system</c>; or to a Proxmox VE node (see <see cref="ProxmoxHost"/>), which is detected.
/// The SSH host key must be trusted (it is shared with SSH sessions).
/// </summary>
internal static partial class KvmHost
{
    private const string Virsh = "virsh -c qemu:///system";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>All VMs (running or not) with their state and graphical display, if any.</summary>
    public static async Task<HypervisorListing> ListAsync(SshClient client, CancellationToken cancellationToken)
    {
        // One round trip: Proxmox's listing on a Proxmox node; otherwise for each VM, its UUID,
        // name, state and display URI, tab-separated.
        const string script =
            "if " + ProxmoxHost.Detect + "; then " + ProxmoxHost.ListScript + "; fi; "
            + "c='" + Virsh + "'; uuids=$($c list --all --uuid) || exit 3; for u in $uuids; do "
            + "printf '%s\\t%s\\t%s\\t%s\\n' \"$u\" \"$($c domname \"$u\")\" \"$($c domstate \"$u\")\" \"$($c domdisplay \"$u\" 2>/dev/null | head -n 1)\"; done";
        var output = await RunAsync(client, script, cancellationToken);
        if (ProxmoxHost.ParseListing(output) is { } proxmox)
        {
            return proxmox;
        }

        var vms = new List<VirtualMachine>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 3 && Guid.TryParse(fields[0], out var uuid))
            {
                var display = fields.ElementAtOrDefault(3) is { Length: > 0 } uri ? uri.Split(':', 2)[0].ToLowerInvariant() : null;
                vms.Add(new VirtualMachine(uuid.ToString("D"), fields[1], fields[2], display is ConsoleProtocols.Spice or ConsoleProtocols.Vnc ? display : null));
            }
        }

        return new HypervisorListing([.. vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)], null);
    }

    /// <summary>The VM's console address on the hypervisor (and password, when libvirt reveals it).</summary>
    /// <param name="protocol">One of <see cref="ConsoleProtocols"/>.</param>
    public static async Task<ConsoleDisplay> GetConsoleDisplayAsync(SshClient client, Guid vm, string protocol, CancellationToken cancellationToken)
    {
        if (protocol is not (ConsoleProtocols.Spice or ConsoleProtocols.Vnc))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unsupported console protocol.");
        }

        // The UUID is formatted by us (never user text) and the protocol is one of ours, so both are safe in the command line.
        var id = vm.ToString("D");
        var output = await RunAsync(
            client,
            $"{Virsh} domdisplay --type {protocol} --include-password {id} 2>/dev/null || {Virsh} domdisplay --type {protocol} {id}",
            cancellationToken);
        var display = ParseDisplayUri(output.Trim().Split('\n')[0].Trim());
        return display is not null && display.Protocol == protocol
            ? display
            : throw new SessionException(
                SessionErrorCodes.ConnectionFailed, $"This VM has no {ConsoleProtocols.DisplayName(protocol)} display, or it isn't running.");
    }

    /// <summary>
    /// Parses libvirt's display URI, e.g. <c>spice://127.0.0.1:5900</c>,
    /// <c>spice://127.0.0.1:5900/?password=secret</c> or <c>vnc://127.0.0.1:0</c>. For VNC, virsh
    /// prints the display number (port 5900 + N), not the port. TLS-only SPICE ports aren't
    /// supported by the browser client.
    /// </summary>
    internal static ConsoleDisplay? ParseDisplayUri(string text)
    {
        var match = DisplayUri().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var protocol = match.Groups["scheme"].Value.ToLowerInvariant();
        var host = match.Groups["host"].Value.Trim('[', ']');
        var query = match.Groups["query"].Value;
        var password = match.Groups["userPassword"].Success ? match.Groups["userPassword"].Value : QueryValue(query, "password");
        if (!match.Groups["port"].Success)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                protocol == ConsoleProtocols.Spice && QueryValue(query, "tls-port") is not null
                    ? "This VM's SPICE display only accepts TLS connections, which the browser console doesn't support yet. Add a plain SPICE port (it can listen on localhost only)."
                    : $"Couldn't read the VM's {ConsoleProtocols.DisplayName(protocol)} address.");
        }

        var port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
        if (protocol == ConsoleProtocols.Vnc)
        {
            port += 5900;
        }

        if (port > 65535)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't read the VM's {ConsoleProtocols.DisplayName(protocol)} address.");
        }

        // A display listening on every address is reachable on the hypervisor's loopback.
        if (host is "" or "0.0.0.0" or "::")
        {
            host = "127.0.0.1";
        }

        return new ConsoleDisplay(protocol, host, port, string.IsNullOrEmpty(password) ? null : Uri.UnescapeDataString(password));
    }

    private static string? QueryValue(string query, string name) =>
        query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name && p.Length == 2)?[1];

    /// <summary>Runs a script and returns its output, or explains why libvirt (or Proxmox) refused.</summary>
    internal static async Task<string> RunAsync(SshClient client, string script, CancellationToken cancellationToken, bool proxmox = false)
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
            if (proxmox || command.ExitStatus == ProxmoxHost.RefusedExitStatus)
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, ProxmoxHost.RefusedMessage(error, command.ExitStatus));
            }

            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                command.ExitStatus == 127 || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? "virsh isn't installed on this host. Install the libvirt client tools (e.g. libvirt-clients or libvirt-client)."
                    : $"libvirt refused the request: {(error.Length > 0 ? error.Split('\n')[0] : $"exit code {command.ExitStatus}")}. The SSH user needs access to qemu:///system (e.g. membership in the libvirt group).");
        }

        return command.Result;
    }

    [GeneratedRegex(@"^(?<scheme>spice|vnc)://(?::(?<userPassword>[^@]*)@)?(?<host>\[[^\]]+\]|[^:/?]*)(?::(?<port>\d{1,5}))?/?(?<query>\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayUri();
}
