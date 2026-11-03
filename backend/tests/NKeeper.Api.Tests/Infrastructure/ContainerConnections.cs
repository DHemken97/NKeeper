using System.Globalization;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// Counts the TCP connections a server in a container has open, read from the kernel's own table
/// (<c>/proc/net/tcp</c>), so a test can prove a session's connection to the host really closed.
/// </summary>
public static class ContainerConnections
{
    /// <summary>Established connections whose local port is <paramref name="port"/> (connections to that server).</summary>
    public static async Task<int> EstablishedAsync(IContainer container, int port)
    {
        var hex = port.ToString("X4", CultureInfo.InvariantCulture);
        var result = await container.ExecAsync(
            ["sh", "-c", $"cat /proc/net/tcp /proc/net/tcp6 2>/dev/null | awk '{{print $2, $4}}' | grep -c ':{hex} 01$' || true"]);
        return int.Parse(result.Stdout.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Waits (up to 15 seconds) until the count is <paramref name="expected"/>; returns the last count seen.</summary>
    public static async Task<int> WaitForAsync(Func<Task<int>> count, int expected, CancellationToken cancellationToken)
    {
        var last = await count();
        for (var attempt = 0; attempt < 75 && last != expected; attempt++)
        {
            await Task.Delay(200, cancellationToken);
            last = await count();
        }

        return last;
    }
}
