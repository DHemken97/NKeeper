using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using NKeeper.Sdk.Sessions;
using NKeeper.Sdk.Web;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// A client for the Zabbix 7.0 JSON-RPC API (<c>api_jsonrpc.php</c>), signing in with an API token
/// in the <c>Authorization: Bearer</c> header. An HTTPS server's certificate is accepted only if it
/// is publicly valid or matches the fingerprint an administrator trusted.
/// </summary>
internal sealed class ZabbixClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _display;
    private readonly string? _apiToken;
    private int _nextId;

    /// <param name="endpoint">The API's address (see <see cref="ZabbixUrl.ApiEndpoint"/>).</param>
    /// <param name="apiToken">The API token; null only for <see cref="GetVersionAsync"/>.</param>
    public ZabbixClient(Uri endpoint, string? apiToken, string? pinnedFingerprint)
    {
        _endpoint = endpoint;
        _display = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (pinnedFingerprint is not null && certificate is not null
                        && string.Equals(WebTargetProbe.Fingerprint(certificate.GetRawCertData()), pinnedFingerprint, StringComparison.OrdinalIgnoreCase)),
            },
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
        _apiToken = apiToken;
    }

    /// <summary>The server's API version, e.g. "7.0.5". Zabbix refuses this call when it carries a token, so it's sent without.</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("apiinfo.version", new { }, cancellationToken, authenticate: false);
        return result.GetString() ?? string.Empty;
    }

    /// <summary>Calls an API method and returns its <c>result</c>.</summary>
    /// <exception cref="SessionException">The server couldn't be reached, refused the token, or answered with an error.</exception>
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken, bool authenticate = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(
                new { jsonrpc = "2.0", method, @params = parameters, id = Interlocked.Increment(ref _nextId) },
                new MediaTypeHeaderValue("application/json-rpc")),
        };
        if (authenticate && _apiToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        using var response = await SendAsync(request, cancellationToken);
        JsonElement body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                response.IsSuccessStatusCode
                    ? $"{_display} didn't answer like a Zabbix API. Check the Zabbix address."
                    : $"{_display} refused the request ({(int)response.StatusCode}). Check the Zabbix address.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{_display} didn't answer like a Zabbix API. Check the Zabbix address.");
        }

        if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw ToException(error);
        }

        return body.TryGetProperty("result", out var result)
            ? result
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{_display} didn't answer like a Zabbix API. Check the Zabbix address.");
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"Zabbix at {_display} didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                ex.InnerException is System.Security.Authentication.AuthenticationException
                    ? $"The certificate of {_display} isn't trusted, or isn't the one trusted in NKeeper, so the connection was refused. Test the settings to trust it."
                    : ex.InnerException is SocketException socket
                        ? $"Couldn't connect to Zabbix at {_display} ({SocketErrors.Describe(socket.SocketErrorCode, _endpoint.Scheme.ToUpperInvariant())})."
                        : $"Couldn't connect to Zabbix at {_display}.");
        }
    }

    /// <summary>Zabbix errors are <c>{code, message, data}</c>; <c>data</c> says what was wrong.</summary>
    private SessionException ToException(JsonElement error)
    {
        var data = error.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var detail = data ?? message ?? "unknown error";
        if (detail.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Not authorised", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("re-login", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("API token expired", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionException(SessionErrorCodes.AuthenticationFailed, $"Zabbix at {_display} refused the API token: {detail}");
        }

        return new SessionException(SessionErrorCodes.ConnectionFailed, $"Zabbix at {_display} answered with an error: {detail}");
    }
}
