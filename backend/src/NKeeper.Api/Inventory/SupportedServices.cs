using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Inventory;

/// <summary>
/// The service types NKeeper can connect with, and so the only ones that can be added to a host.
/// The others (SSMS, SFTP, FTP, WinRM, PowerShell, Other) are still read from the database,
/// where older versions or imports may have stored them, but can't be added any more.
/// </summary>
internal static class SupportedServices
{
    private static readonly HashSet<ServiceProtocol> Protocols =
    [
        ServiceProtocol.Ssh,
        ServiceProtocol.Rdp,
        ServiceProtocol.Rpc,
        ServiceProtocol.Vnc,
        ServiceProtocol.Telnet,
        ServiceProtocol.Http,
        ServiceProtocol.Https,
        ServiceProtocol.Kvm,
        ServiceProtocol.Idrac,
        ServiceProtocol.Proxmox,
    ];

    public static bool IsSupported(ServiceProtocol protocol) => Protocols.Contains(protocol);
}
