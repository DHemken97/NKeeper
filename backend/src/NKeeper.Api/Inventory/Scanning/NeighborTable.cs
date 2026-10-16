using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// The NKeeper server's neighbour (ARP) table: a host's MAC address is only known for hosts on the
/// same network segment as the server, after something has talked to them.
/// </summary>
internal static class NeighborTable
{
    /// <returns>The MAC address as AA:BB:CC:DD:EE:FF, or null when it isn't known.</returns>
    public static string? FindMac(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                return FromProcArp(File.ReadAllLines("/proc/net/arp"), address);
            }

            if (OperatingSystem.IsWindows())
            {
                return FromSendArp(address);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// /proc/net/arp: "IP address  HW type  Flags  HW address  Mask  Device"; flag 0x2 marks a
    /// complete entry.
    /// </summary>
    internal static string? FromProcArp(IEnumerable<string> lines, IPAddress address)
    {
        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4
                && IPAddress.TryParse(columns[0], out var ip) && ip.Equals(address)
                && int.TryParse(columns[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags)
                && (flags & 0x2) != 0
                && NetworkAddress.TryNormalizeMac(columns[3], out var mac)
                && mac != "00:00:00:00:00:00")
            {
                return mac;
            }
        }

        return null;
    }

    /// <summary>Windows: SendARP answers from the ARP cache, or asks on the local segment.</summary>
    private static string? FromSendArp(IPAddress address)
    {
        var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        ulong mac = 0;
        uint length = 6;
        if (NativeMethods.SendARP(destination, 0, ref mac, ref length) != 0 || length != 6 || mac == 0)
        {
            return null;
        }

        var bytes = BitConverter.GetBytes(mac);
        return string.Join(':', bytes.Take(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static class NativeMethods
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SendARP(uint destIp, uint srcIp, ref ulong macAddress, ref uint macAddressLength);
    }
}
