namespace NKeeper.Core.Entities;

public enum ConnectionProtocol
{
    Ssh,
    Rdp,
    Vnc,
    Telnet,
    Http,
    WinRm
}

/// <summary>
/// How to reach a node over one protocol. Protocol modules own the shape of
/// <see cref="SettingsJson"/>, so new options don't require schema changes.
/// </summary>
public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid NodeId { get; set; }

    public Node Node { get; set; } = null!;

    public required string Name { get; set; }

    public ConnectionProtocol Protocol { get; set; }

    public int Port { get; set; }

    public string? Username { get; set; }

    /// <summary>Protocol-specific options (e.g. RDP resolution, SSH key id) as a JSON object.</summary>
    public string SettingsJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => 22,
        ConnectionProtocol.Rdp => 3389,
        ConnectionProtocol.Vnc => 5900,
        ConnectionProtocol.Telnet => 23,
        ConnectionProtocol.Http => 443,
        ConnectionProtocol.WinRm => 5986,
        _ => 0
    };
}
