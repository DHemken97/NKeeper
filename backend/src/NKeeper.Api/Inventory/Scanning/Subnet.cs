using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>An IPv4 network to scan, in CIDR form (e.g. 192.168.1.0/24).</summary>
internal sealed record Subnet(uint Network, int Prefix)
{
    /// <summary>The smallest prefix a scan accepts: a /20, 4,096 addresses.</summary>
    public const int SmallestPrefix = 20;

    /// <summary>How many addresses a scan probes: the network and broadcast addresses are left out (except in a /31 or /32).</summary>
    public int Count => Prefix >= 31 ? 1 << (32 - Prefix) : (1 << (32 - Prefix)) - 2;

    /// <summary>
    /// Accepts "10.0.0.0/24", an address inside the network ("10.0.0.7/24", taken as its network), or a
    /// single address ("10.0.0.7", a /32).
    /// </summary>
    /// <param name="error">Why it was refused, for the user.</param>
    public static bool TryParse(string? text, out Subnet subnet, out string error)
    {
        subnet = new Subnet(0, 32);
        error = string.Empty;
        var parts = (text ?? string.Empty).Trim().Split('/');
        if (parts.Length > 2 || !NetworkAddress.TryNormalizeIp(parts[0], out var ipText) || !IPAddress.TryParse(ipText, out var ip))
        {
            error = "Enter an IPv4 network in CIDR form, such as 192.168.1.0/24.";
            return false;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Only IPv4 networks can be scanned (an IPv6 subnet is far too large to sweep).";
            return false;
        }

        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > 32))
        {
            error = "The prefix length must be a number from 20 to 32, such as /24.";
            return false;
        }

        if (prefix < SmallestPrefix)
        {
            error = $"A /{prefix} is {1L << (32 - prefix):N0} addresses; a scan covers at most 4,096 (a /20). Scan it in parts.";
            return false;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        subnet = new Subnet(ToUInt(ip) & mask, prefix);
        return true;
    }

    /// <summary>The addresses to probe, in order.</summary>
    public IEnumerable<IPAddress> Addresses()
    {
        var size = 1u << (32 - Prefix);
        var (first, last) = Prefix >= 31 ? (0u, size - 1) : (1u, size - 2);
        for (var offset = first; offset <= last; offset++)
        {
            yield return FromUInt(Network + offset);
        }
    }

    public override string ToString() => $"{FromUInt(Network)}/{Prefix}";

    /// <summary>The IPv4 networks the NKeeper server itself is on, to suggest as scan targets.</summary>
    public static IReadOnlyList<(Subnet Subnet, string Interface, string Address)> Local()
    {
        var found = new List<(Subnet, string, string)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)
                        || unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // On a network wider than a /24, the /24 around the server is suggested: a sweep of
                    // the whole of it would be slow (and is often refused as too large).
                    var prefix = Math.Max(unicast.PrefixLength, 24);
                    if (TryParse($"{unicast.Address}/{prefix}", out var subnet, out _))
                    {
                        found.Add((subnet, nic.Name, unicast.Address.ToString()));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return [.. found.DistinctBy(f => f.Item1)];
    }

    internal static uint ToUInt(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    internal static IPAddress FromUInt(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
