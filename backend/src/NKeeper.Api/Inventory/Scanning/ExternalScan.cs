using System.Net;
using System.Net.Sockets;
using System.Text;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// An external scan, from the NKeeper server over the network: TCP connections to common ports
/// (and the host's own service ports) on the host's saved address, the SSH banner, reverse DNS,
/// and the MAC address when the host shares the server's network segment. Only the host's own
/// address is ever scanned. This is visible on the network as a port scan.
/// </summary>
internal sealed class ExternalScan(ILogger<ExternalScan> logger)
{
    private const int Parallelism = 32;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Scans the host once; at most a few run on the server at a time.</summary>
    private static readonly SemaphoreSlim Running = new(4);

    public async Task<ScanFindings> ScanAsync(Host host, CancellationToken cancellationToken)
    {
        if (!await Running.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
        {
            throw new SessionException(SessionErrorCodes.TooManySessions, "Other scans are running. Try again in a moment.");
        }

        try
        {
            return await RunAsync(host, cancellationToken);
        }
        finally
        {
            Running.Release();
        }
    }

    private async Task<ScanFindings> RunAsync(Host host, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var address = await ResolveAsync(host, cancellationToken);

        var ports = ScanPorts.Common
            .Concat(host.Services.Select(s => s.Port).OfType<int>())
            .Distinct()
            .Order()
            .ToList();
        var open = new List<int>();
        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (port, token) =>
            {
                if (await IsOpenAsync(address, port, token))
                {
                    lock (open)
                    {
                        open.Add(port);
                    }
                }
            });
        open.Sort();
        logger.LogInformation("External scan of host {HostId} ({Address}): {Open} of {Probed} ports open", host.Id, address, open.Count, ports.Count);

        string? os = null;
        var sshPorts = host.Services.Where(s => s.Protocol == ServiceProtocol.Ssh).Select(s => s.Port ?? 22).Append(22);
        foreach (var port in sshPorts.Distinct().Where(open.Contains))
        {
            if (await ReadBannerAsync(address, port, cancellationToken) is { } banner)
            {
                notes.Add($"SSH on port {port} says: {banner}");
                os ??= OsFromSshBanner(banner);
            }
        }

        if (os is null && (open.Contains(3389) || open.Contains(445) || open.Contains(135)))
        {
            os = "Windows";
            notes.Add("The operating system is a guess from the open ports (RDP, SMB or Microsoft RPC).");
        }
        else if (os is not null)
        {
            notes.Add("The operating system is a guess from the SSH banner.");
        }

        var mac = NeighborTable.FindMac(address);
        if (mac is null)
        {
            notes.Add("No MAC address: it's only visible for hosts on the same network segment as the NKeeper server (not through a router, a VPN or a container network).");
        }

        var hostname = await ReverseDnsAsync(address, cancellationToken);
        if (open.Count == 0)
        {
            notes.Add("No open ports were found. The host may be down, or a firewall drops connections.");
        }

        return new ScanFindings(hostname, address.ToString(), mac, os, [.. open.Select(ScanPorts.Describe)], false, [], notes);
    }

    private static async Task<IPAddress> ResolveAsync(Host host, CancellationToken cancellationToken)
    {
        if (host.IpAddress is { } ip && IPAddress.TryParse(ip, out var parsed))
        {
            return parsed;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DnsTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host.Hostname!, timeout.Token);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SessionException(SessionErrorCodes.HostNotFound, $"“{host.Hostname}” couldn't be resolved to an address.");
        }
    }

    private static async Task<bool> IsOpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>The SSH server's identification line, e.g. "SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5".</summary>
    private static async Task<string?> ReadBannerAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BannerTimeout);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            var buffer = new byte[256];
            var read = await client.GetStream().ReadAsync(buffer, timeout.Token);
            var line = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim();
            return line.StartsWith("SSH-", StringComparison.Ordinal) ? line : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static string? OsFromSshBanner(string banner)
    {
        string[] known = ["Ubuntu", "Debian", "Raspbian", "FreeBSD", "OpenBSD", "NetBSD"];
        if (banner.Contains("OpenSSH_for_Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        return known.FirstOrDefault(name => banner.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DnsTimeout);
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token);
            return entry.HostName != address.ToString() && NetworkAddress.IsValidHostname(entry.HostName) ? entry.HostName.TrimEnd('.') : null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
