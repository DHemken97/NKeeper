using NKeeper.Sdk.Plugins;

namespace NKeeper.Plugins.Idrac;

/// <summary>
/// Dell iDRACs (an iDRAC service, at its own management address): the server's status and power
/// over Redfish, and the HTML5 virtual console in a web session. RACADM is NKeeper's own SSH session
/// on the iDRAC service.
/// </summary>
[NKeeperPlugin(Id, "Dell iDRAC")]
public sealed class IdracPlugin : INKeeperPlugin
{
    public const string Id = "idrac";

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }
}
