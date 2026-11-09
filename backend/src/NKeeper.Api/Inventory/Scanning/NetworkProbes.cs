using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>What a TCP connection attempt says about the address.</summary>
internal enum TcpAnswer
{
    /// <summary>Nothing came back: no machine, or a firewall that drops connections.</summary>
    Silent,

    /// <summary>The machine refused the connection (a reset): it exists, but nothing listens there.</summary>
    Refused,

    Open,
}

/// <summary>The single probes a network scan is built from. Each one is short and gives up quietly.</summary>
internal static partial class NetworkProbes
{
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NetBiosTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    /// <returns>Whether the address answered an ICMP echo, and the reply's TTL when the platform reports it.</returns>
    public static async Task<(bool Answered, int? Ttl)> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success ? (true, reply.Options?.Ttl is > 0 and var ttl ? ttl : null) : (false, null);
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException or PlatformNotSupportedException)
        {
            // No ICMP here (e.g. a container without it): the TCP and ARP probes still find machines.
            return (false, null);
        }
    }

    public static async Task<TcpAnswer> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(address, port, linked.Token);
            return TcpAnswer.Open;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return TcpAnswer.Refused;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TcpAnswer.Silent;
        }
    }

    /// <summary>The SSH server's identification line, e.g. "SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5".</summary>
    public static async Task<string?> SshBannerAsync(IPAddress address, int port, CancellationToken cancellationToken)
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

    public static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken cancellationToken)
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

    /// <summary>A web server's answer: its Server header, page title, redirect, and (HTTPS) certificate.</summary>
    public sealed record WebAnswer(int Status, string? Server, string? Title, string? Location, string? CertificateSubject, IReadOnlyList<string> CertificateNames);

    /// <summary>One GET of the server's root page. Redirects aren't followed; certificates aren't checked (only read).</summary>
    public static async Task<WebAnswer?> WebAsync(IPAddress address, int port, bool https, CancellationToken cancellationToken)
    {
        X509Certificate2? certificate = null;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            // The connection only reads what the server presents about itself; nothing is sent but a GET.
#pragma warning disable CA5359, CA5400
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                certificate ??= cert is null ? null : new X509Certificate2(cert);
                return true;
            },
            CheckCertificateRevocationList = false,
#pragma warning restore CA5359, CA5400
        };
        using var client = new HttpClient(handler) { Timeout = HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NKeeper-NetworkScan", "1.0"));
        var host = address.ToString();
        try
        {
            using var response = await client.GetAsync(new Uri($"{(https ? "https" : "http")}://{host}:{port}/"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var server = response.Headers.Server.Count > 0 ? response.Headers.Server.ToString() : null;
            string? title = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[32 * 1024];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
                {
                    total += read;
                }

                title = TitleOf(Encoding.UTF8.GetString(buffer, 0, total));
            }

            using (certificate)
            {
                return new WebAnswer(
                    (int)response.StatusCode,
                    Shorten(server),
                    title,
                    Shorten(response.Headers.Location?.ToString()),
                    certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    certificate is null ? [] : CertificateNames(certificate));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            certificate?.Dispose();
            return null;
        }
    }

    /// <summary>The certificate's DNS names: its subject's common name and its subject alternative names.</summary>
    internal static IReadOnlyList<string> CertificateNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        if (certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false) is { Length: > 0 } cn)
        {
            names.Add(cn);
        }

        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                names.AddRange(san.EnumerateDnsNames());
            }
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A name from a certificate or banner that could be the machine's own hostname (not a wildcard, an address or localhost).</summary>
    internal static bool LooksLikeHostname(string? name) =>
        name is { Length: > 0 }
        && !name.Contains('*', StringComparison.Ordinal)
        && !IPAddress.TryParse(name, out _)
        && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("localhost.", StringComparison.OrdinalIgnoreCase)
        && NetworkAddress.IsValidHostname(name);

    internal static string? TitleOf(string html)
    {
        Match match;
        try
        {
            match = TitlePattern().Match(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        var title = WebUtility.HtmlDecode(Whitespace().Replace(match.Groups[1].Value, " ")).Trim();
        return title.Length == 0 ? null : Shorten(title);
    }

    private static string? Shorten(string? text) => text is { Length: > 120 } ? text[..117] + "…" : text;

    /// <summary>What a NetBIOS node status query found: the machine's NetBIOS name, its workgroup or domain, and its MAC address.</summary>
    public sealed record NetBiosAnswer(string? Name, string? Workgroup, string? MacAddress);

    /// <summary>A NetBIOS node status (NBSTAT) query to UDP 137: Windows machines and Samba servers answer it.</summary>
    public static async Task<NetBiosAnswer?> NetBiosAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetBiosTimeout);
        try
        {
            var transaction = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            await udp.SendAsync(NodeStatusRequest(transaction), new IPEndPoint(address, 137), timeout.Token);
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                if (result.RemoteEndPoint.Address.Equals(address) && ParseNodeStatus(result.Buffer, transaction) is { } answer)
                {
                    return answer;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>An NBSTAT query for the wildcard name "*" (RFC 1002 4.2.17).</summary>
    internal static byte[] NodeStatusRequest(ushort transaction)
    {
        var packet = new byte[50];
        packet[0] = (byte)(transaction >> 8);
        packet[1] = (byte)transaction;
        packet[5] = 1; // one question
        packet[12] = 0x20; // the encoded name is 32 bytes
        // "*" padded with zeros, first-level encoded: each nibble becomes 'A' + nibble.
        packet[13] = (byte)('A' + ('*' >> 4));
        packet[14] = (byte)('A' + ('*' & 0xF));
        for (var i = 15; i < 45; i++)
        {
            packet[i] = (byte)'A';
        }

        // packet[45] = 0: the name ends.
        packet[47] = 0x21; // type NBSTAT
        packet[49] = 0x01; // class IN
        return packet;
    }

    /// <summary>Reads an NBSTAT response: the unique workstation name (suffix 0x00), the group name (the workgroup or domain), and the unit ID (the MAC address).</summary>
    internal static NetBiosAnswer? ParseNodeStatus(byte[] packet, ushort transaction)
    {
        if (packet.Length < 57 || packet[0] != (byte)(transaction >> 8) || packet[1] != (byte)transaction || (packet[2] & 0x80) == 0)
        {
            return null;
        }

        // The answer's name: a length-prefixed label sequence, or a compression pointer.
        var offset = 12;
        while (offset < packet.Length && packet[offset] != 0)
        {
            if ((packet[offset] & 0xC0) == 0xC0)
            {
                offset++;
                break;
            }

            offset += packet[offset] + 1;
        }

        offset += 1 + 2 + 2 + 4 + 2; // end of name, type, class, TTL, data length
        if (offset >= packet.Length)
        {
            return null;
        }

        int count = packet[offset++];
        string? name = null;
        string? workgroup = null;
        for (var i = 0; i < count && offset + 18 <= packet.Length; i++, offset += 18)
        {
            var entry = Encoding.ASCII.GetString(packet, offset, 15).TrimEnd(' ', '\0');
            var suffix = packet[offset + 15];
            var group = (packet[offset + 16] & 0x80) != 0;
            if (suffix == 0x00 && entry.Length > 0 && !entry.StartsWith("__MSBROWSE__", StringComparison.Ordinal))
            {
                if (group)
                {
                    workgroup ??= entry;
                }
                else
                {
                    name ??= entry;
                }
            }
        }

        string? mac = null;
        if (offset + 6 <= packet.Length)
        {
            var unitId = string.Join(':', packet.Skip(offset).Take(6).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            // Samba reports zeros.
            mac = unitId == "00:00:00:00:00:00" ? null : unitId;
        }

        return name is null && workgroup is null ? null : new NetBiosAnswer(name, workgroup, mac);
    }

    /// <summary>The usual starting TTL of the reply, and what it hints at. Routers on the way lower it a little.</summary>
    internal static string? TtlHint(int? ttl) => ttl switch
    {
        null or <= 0 => null,
        <= 64 => "Linux, a Unix-like system or an embedded device",
        <= 128 => "Windows",
        _ => "a network device (router, switch, printer) or Solaris",
    };

    /// <summary>The maker of a network card, for prefixes that say what kind of machine it is (mostly virtual machines).</summary>
    /// <returns>The vendor, and whether a machine with it is a virtual machine (or container).</returns>
    internal static (string? Vendor, bool Virtual) MacVendor(string? mac)
    {
        if (mac is not { Length: 17 })
        {
            return (null, false);
        }

        var prefix = mac[..8].ToUpperInvariant();
        if (KnownVendors.TryGetValue(prefix, out var known))
        {
            return known;
        }

        if (prefix.StartsWith("02:42:", StringComparison.Ordinal))
        {
            return ("Docker container", true);
        }

        // The locally administered bit: a randomized ("private") address, or one software made up.
        return (Convert.ToByte(mac[..2], 16) & 0x02) != 0 ? ("Randomized or locally administered", false) : (null, false);
    }

    private static readonly Dictionary<string, (string?, bool)> KnownVendors = new(StringComparer.Ordinal)
    {
        ["00:50:56"] = ("VMware", true),
        ["00:0C:29"] = ("VMware", true),
        ["00:05:69"] = ("VMware", true),
        ["00:15:5D"] = ("Microsoft Hyper-V", true),
        ["52:54:00"] = ("QEMU / KVM", true),
        ["BC:24:11"] = ("Proxmox VE", true),
        ["08:00:27"] = ("VirtualBox", true),
        ["00:16:3E"] = ("Xen", true),
        ["00:1C:42"] = ("Parallels", true),
        ["B8:27:EB"] = ("Raspberry Pi", false),
        ["DC:A6:32"] = ("Raspberry Pi", false),
        ["E4:5F:01"] = ("Raspberry Pi", false),
        ["2C:CF:67"] = ("Raspberry Pi", false),
        ["D8:3A:DD"] = ("Raspberry Pi", false),
        ["00:11:32"] = ("Synology", false),
    };

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 500)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
