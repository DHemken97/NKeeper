using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NKeeper.Api.Inventory;

/// <summary>Validation and normalization for host addresses.</summary>
internal static partial class NetworkAddress
{
    /// <summary>Accepts dotted-quad IPv4 or IPv6; returns the canonical text form.</summary>
    public static bool TryNormalizeIp(string value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = value.Trim();

        // IPAddress.TryParse alone accepts shorthand such as "10" or "10.1", which is never intended here.
        var isIpv6 = candidate.Contains(':', StringComparison.Ordinal);
        if (!isIpv6 && !Ipv4Pattern().IsMatch(candidate))
        {
            return false;
        }

        if (!IPAddress.TryParse(candidate, out var address)
            || address.AddressFamily != (isIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    /// <summary>RFC 1123 host name; underscores are tolerated because Windows networks use them.</summary>
    public static bool IsValidHostname(string value) => HostnamePattern().IsMatch(value.Trim());

    /// <summary>Accepts 12 hex digits with ':', '-', '.' or no separators; returns AA:BB:CC:DD:EE:FF.</summary>
    public static bool TryNormalizeMac(string value, out string normalized)
    {
        normalized = string.Empty;
        var hex = MacSeparators().Replace(value.Trim(), string.Empty);
        if (!MacHex().IsMatch(hex))
        {
            return false;
        }

        normalized = string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant();
        return true;
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"^(?=.{1,253}\.?$)[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(?:\.[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*\.?$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex(@"[:\-.\s]")]
    private static partial Regex MacSeparators();

    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacHex();
}
