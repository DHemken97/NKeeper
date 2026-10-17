using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NKeeper.Api.Controllers;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Web;

namespace NKeeper.Api.Idrac;

/// <summary>
/// A Redfish API client for one BMC (an iDRAC), authenticating with HTTP Basic on every request.
/// The BMC's certificate is accepted only if it is publicly valid or matches the fingerprint the
/// user trusted, exactly like web sessions.
/// </summary>
internal sealed class RedfishClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Redfish property names are PascalCase (e.g. ResetType), so names are sent as declared.</summary>
    private static readonly JsonSerializerOptions BodyJson = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly string _display;

    /// <param name="tunnel">A jump-host tunnel to dial instead of the iDRAC's address.</param>
    public RedfishClient(SessionTarget target, string? pinnedFingerprint, JumpTunnel? tunnel = null)
    {
        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        _display = $"{target.Address}:{target.Port}";
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = tunnel is { IsTunnel: true } ? (_, token) => JumpTunnel.ConnectStreamAsync(tunnel, token) : null,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (pinnedFingerprint is not null && certificate is not null
                        && string.Equals(WebTargetProbe.Fingerprint(certificate.GetRawCertData()), pinnedFingerprint, StringComparison.OrdinalIgnoreCase)),
            },
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = WebSessionsController.TargetUri(https: true, target.Address, target.Port),
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.Username}:{target.Password}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>GETs a Redfish resource.</summary>
    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        await TryGetAsync(path, cancellationToken)
        ?? throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The BMC at {_display} has no Redfish resource {path}.");

    /// <summary>GETs a Redfish resource, or null when the BMC doesn't have it (404, or a query it doesn't support).</summary>
    public async Task<JsonElement?> TryGetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>POSTs an action; returns the response body (an empty object when there is none).</summary>
    public async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: BodyJson) }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                return await _http.SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SessionException(SessionErrorCodes.Timeout, $"The iDRAC at {_display} didn't answer in time.");
            }
            catch (HttpRequestException ex)
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed,
                    ex.InnerException is System.Security.Authentication.AuthenticationException
                        ? $"The certificate of {_display} didn't match the one trusted in NKeeper, so the connection was refused."
                        : ex.InnerException is SocketException socket
                            ? $"Couldn't connect to the iDRAC at {_display} ({SocketErrors.Describe(socket.SocketErrorCode, "HTTPS")})."
                            : $"Couldn't connect to the iDRAC at {_display}.");
            }
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SessionException(SessionErrorCodes.AuthenticationFailed, $"The iDRAC at {_display} refused the username or password.");
        }

        var message = ErrorMessage(await ReadAsync(response, cancellationToken));
        throw new SessionException(
            SessionErrorCodes.ConnectionFailed,
            response.StatusCode == HttpStatusCode.Forbidden
                ? $"The iDRAC account isn't allowed to do this{(message is null ? "." : $": {message}")}"
                : $"The iDRAC refused the request ({(int)response.StatusCode}){(message is null ? "." : $": {message}")}");
    }

    /// <summary>The first message of a Redfish error body, if there is one.</summary>
    private static string? ErrorMessage(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (error.TryGetProperty("@Message.ExtendedInfo", out var info) && info.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in info.EnumerateArray())
            {
                if (Redfish.String(item, "Message") is { } extended)
                {
                    return extended;
                }
            }
        }

        return Redfish.String(error, "message");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement;
        }

        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            if (response.IsSuccessStatusCode)
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, "The iDRAC's answer wasn't Redfish JSON. Is this an iDRAC (or another Redfish BMC)?");
            }

            return JsonDocument.Parse("{}").RootElement;
        }
    }
}

/// <summary>Reading Redfish JSON, where any property may be missing or null.</summary>
internal static class Redfish
{
    public static string? String(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    public static double? Number(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public static JsonElement? Find(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element;
    }

    /// <summary>The @odata.id of a collection's first member.</summary>
    public static string? FirstMember(JsonElement collection) =>
        Find(collection, "Members") is { ValueKind: JsonValueKind.Array } members && members.GetArrayLength() > 0
            ? String(members[0], "@odata.id")
            : null;
}
