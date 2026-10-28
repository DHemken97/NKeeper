namespace NKeeper.Sdk.Consoles;

/// <summary>The console protocols NKeeper opens for VMs (libvirt's display URI schemes).</summary>
public static class ConsoleProtocols
{
    public const string Spice = "spice";
    public const string Vnc = "vnc";

    public static string DisplayName(string protocol) => protocol.ToUpperInvariant();
}

/// <summary>Where a VM's console (SPICE or VNC) listens on the hypervisor, and its password if it has one.</summary>
public sealed record ConsoleDisplay(string Protocol, string Host, int Port, string? Password);
