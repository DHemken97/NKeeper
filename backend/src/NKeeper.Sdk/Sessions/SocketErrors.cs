using System.Net.Sockets;

namespace NKeeper.Sdk.Sessions;

public static class SocketErrors
{
    /// <summary>A short, user-facing reason for a failed TCP connection.</summary>
    public static string Describe(SocketError error, string protocolName) => error switch
    {
        SocketError.ConnectionRefused => $"connection refused; is {protocolName} enabled on that port?",
        SocketError.HostNotFound or SocketError.NoData => "the name could not be resolved",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "the host is unreachable",
        SocketError.TimedOut => "timed out",
        _ => error.ToString(),
    };
}
