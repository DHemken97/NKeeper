using NKeeper.Sdk.Plugins;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// Zabbix: links NKeeper hosts to Zabbix hosts (the Zabbix host ID is a per-host field), reads what
/// Zabbix knows about them, and compares the two inventories. NKeeper only reads from Zabbix.
/// </summary>
[NKeeperPlugin(Id, "Zabbix")]
public sealed class ZabbixPlugin : INKeeperPlugin
{
    public const string Id = "zabbix";

    /// <summary>The host field holding the linked Zabbix host's ID.</summary>
    public const string HostIdField = "hostId";

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        context.AddHostField(new HostField(HostIdField, ZabbixSettings.HostIdMaxLength, "[0-9]+", "A Zabbix host ID is a number, e.g. 10084."));
        services.AddScoped<ZabbixConnection>();
    }
}
