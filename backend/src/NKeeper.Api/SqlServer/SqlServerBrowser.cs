using System.Globalization;
using System.Net.Sockets;
using System.Text;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// Asks a host's SQL Server Browser (UDP 1434) which TCP port a named instance listens on, as
/// SqlClient does for <c>host\instance</c>. NKeeper needs the port itself to read the server's
/// certificate before signing in.
/// </summary>
internal static class SqlServerBrowser
{
    public const int Port = 1434;
    private const byte InstanceRequest = 0x04;
    private const byte Response = 0x05;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <exception cref="SessionException">The browser didn't answer, or doesn't know the instance or its TCP port.</exception>
    public static async Task<int> ResolvePortAsync(string address, string instance, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var udp = new UdpClient(address.Contains(':', StringComparison.Ordinal) ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            udp.Connect(address, Port);
            var request = new byte[instance.Length + 2];
            request[0] = InstanceRequest;
            Encoding.ASCII.GetBytes(instance, 0, instance.Length, request, 1);
            await udp.SendAsync(request, timeout.Token);
            var answer = await udp.ReceiveAsync(timeout.Token);
            return ReadTcpPort(answer.Buffer)
                ?? throw new SessionException(
                    SessionErrorCodes.ConnectionFailed,
                    $"The SQL Server Browser on {address} doesn't know an instance “{instance}” listening on TCP. Check the instance name, or set its port on the service.");
        }
        catch (SocketException ex)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't ask the SQL Server Browser on {address} for “{instance}” ({ex.SocketErrorCode}). Set the instance's port on the service instead.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(
                SessionErrorCodes.Timeout,
                $"The SQL Server Browser on {address} didn't answer for “{instance}”. Is the SQL Server Browser service running (UDP {Port})? Or set the instance's port on the service.");
        }
    }

    /// <summary>The <c>tcp</c> value of a browser response (<c>ServerName;X;InstanceName;Y;…;tcp;1433;;</c>).</summary>
    internal static int? ReadTcpPort(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 3 || answer[0] != Response)
        {
            return null;
        }

        var fields = Encoding.ASCII.GetString(answer[3..]).Split(';');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (string.Equals(fields[i], "tcp", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
            {
                return port;
            }
        }

        return null;
    }
}
