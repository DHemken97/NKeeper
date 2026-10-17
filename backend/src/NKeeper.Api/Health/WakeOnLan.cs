using System.Net;
using System.Net.Sockets;

namespace NKeeper.Api.Health;

/// <summary>
/// Sends Wake-on-LAN "magic packets" (six 0xFF bytes, then the MAC address sixteen times) from the
/// NKeeper server, over UDP port 9: to the local broadcast address, and, when the host has an IPv4
/// address, to its /24 subnet's broadcast address and to the address itself (for routers that
/// forward directed broadcasts or keep a static ARP entry).
/// </summary>
internal static class WakeOnLan
{
    private const int Port = 9;

    public static byte[] MagicPacket(string mac)
    {
        var bytes = mac.Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
        if (bytes.Length != 6)
        {
            throw new ArgumentException("A MAC address has six bytes.", nameof(mac));
        }

        var packet = new byte[6 + (16 * 6)];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            bytes.CopyTo(packet, 6 + (i * 6));
        }

        return packet;
    }

    /// <returns>Where the packet was sent, for the audit trail.</returns>
    public static async Task<List<string>> SendAsync(string mac, string? ipAddress, CancellationToken cancellationToken)
    {
        var packet = MagicPacket(mac);
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (ipAddress is not null && IPAddress.TryParse(ipAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = ip.GetAddressBytes();
            targets.Add(new IPAddress([octets[0], octets[1], octets[2], 255]));
            targets.Add(ip);
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        var sent = new List<string>();
        foreach (var target in targets.Distinct())
        {
            try
            {
                await udp.SendAsync(packet, new IPEndPoint(target, Port), cancellationToken);
                sent.Add(target.ToString());
            }
            catch (SocketException)
            {
                // e.g. no route for a directed broadcast: the other targets may still work.
            }
        }

        return sent;
    }
}
