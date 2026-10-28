using NKeeper.Api.Configuration;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Api.RemoteSessions;

/// <summary>
/// Defends against cross-site WebSocket hijacking. Browsers send cookies with WebSocket
/// handshakes but CORS does not apply to them, so the Origin must be checked explicitly:
/// it has to be this site itself, or one of the configured frontend origins.
/// </summary>
internal static class WebSocketOrigin
{
    public static bool IsAllowed(HttpRequest request, CorsSettings cors)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Not a browser (browsers always send Origin on WebSocket handshakes); the
            // session cookie is still required.
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        if (cors.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Same origin: compare host and port (the proxy forwards the browser's Host header).
        return string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || (originUri.IsDefaultPort && string.Equals(originUri.Host, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }
}
