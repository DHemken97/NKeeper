namespace NKeeper.Api.Zabbix;

/// <summary>Problem-detail codes for Zabbix requests, besides the session ones (connection_failed, authentication_failed, timeout).</summary>
public static class ZabbixErrorCodes
{
    /// <summary>Zabbix isn't set up, or is turned off, under Settings.</summary>
    public const string Disabled = "zabbix_disabled";

    /// <summary>The NKeeper host isn't linked to a Zabbix host.</summary>
    public const string NotLinked = "zabbix_not_linked";

    /// <summary>The linked Zabbix host doesn't exist (any more), or the API token can't see it.</summary>
    public const string HostNotFound = "zabbix_host_not_found";
}
