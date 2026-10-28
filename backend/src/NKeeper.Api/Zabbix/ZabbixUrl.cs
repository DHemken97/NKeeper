namespace NKeeper.Api.Zabbix;

/// <summary>The Zabbix frontend's address as administrators paste it, and the API endpoint under it.</summary>
public static class ZabbixUrl
{
    public const string ApiPath = "api_jsonrpc.php";

    /// <summary>
    /// Normalizes an address like <c>https://zabbix.example.com/zabbix/zabbix.php?action=dashboard.view</c>
    /// to the frontend's base, <c>https://zabbix.example.com/zabbix/</c>: no query or fragment, and
    /// without a trailing <c>.php</c> page (such as <c>api_jsonrpc.php</c> itself).
    /// </summary>
    public static bool TryNormalize(string? input, out Uri frontend)
    {
        frontend = null!;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        if (path[(lastSlash + 1)..].EndsWith(".php", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..(lastSlash + 1)];
        }

        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        frontend = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri;
        return true;
    }

    public static Uri ApiEndpoint(Uri frontend) => new(frontend, ApiPath);
}
