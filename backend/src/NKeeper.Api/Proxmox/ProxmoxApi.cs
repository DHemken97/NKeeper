using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Api.Controllers;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Web;

namespace NKeeper.Api.Proxmox;

/// <summary>A VM (qemu) or container (lxc) of a Proxmox cluster, as the API lists it.</summary>
internal sealed record ProxmoxGuest(int VmId, string Type, string Node, string Name, string Status);

/// <summary>A console the API opened: connect to its WebSocket with <see cref="Ticket"/>, which is also the VNC password.</summary>
internal sealed record VncProxyTicket(string Port, string Ticket);

/// <summary>
/// The Proxmox VE API of one node (HTTPS, port 8006), signed in with a Proxmox login
/// (<c>user@realm</c>, realm <c>pam</c> by default) or an API token (<c>user@realm!token</c>, its
/// secret as the password). Any node answers for the whole cluster. The certificate is accepted
/// only if it is publicly valid or matches the fingerprint the user trusted, like web sessions.
/// </summary>
internal sealed partial class ProxmoxApi : IDisposable
{
    public const int DefaultPort = 8006;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly string _display;
    private readonly string _userId;
    private readonly string _password;

    /// <summary>The API token header, or the ticket cookie and CSRF token after signing in.</summary>
    private string? _token;
    private string? _ticket;
    private string? _csrf;

    /// <param name="tunnel">A jump-host tunnel to dial instead of the node's address.</param>
    public ProxmoxApi(SessionTarget target, string? pinnedFingerprint, JumpTunnel? tunnel = null)
    {
        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        _display = $"{target.Address}:{target.Port}";
        _userId = UserId(target.Username, target.Domain);
        _password = target.Password;
        if (_userId.Contains('!', StringComparison.Ordinal))
        {
            _token = $"PVEAPIToken={_userId}={_password}";
        }

        _handler = new SocketsHttpHandler
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
        _baseAddress = WebSessionsController.TargetUri(https: true, target.Address, target.Port);
        _http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = _baseAddress, Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// The Proxmox user ID for a credential: <c>user@realm</c> as given, else with the credential's
    /// domain or <c>pam</c> as the realm. An API token (<c>user!token</c>) gets the realm before the <c>!</c>.
    /// </summary>
    internal static string UserId(string username, string? domain)
    {
        var name = username.Trim();
        var bang = name.IndexOf('!', StringComparison.Ordinal);
        var user = bang < 0 ? name : name[..bang];
        if (!user.Contains('@', StringComparison.Ordinal))
        {
            user = $"{user}@{(string.IsNullOrWhiteSpace(domain) ? "pam" : domain.Trim())}";
        }

        return bang < 0 ? user : user + name[bang..];
    }

    /// <summary>Signs in with the password (a ticket); API tokens need no sign-in.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (_token is not null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api2/json/access/ticket")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = _userId, ["password"] = _password }),
        };
        var data = await SendAsync(request, cancellationToken, signingIn: true);
        if ((data.TryGetProperty("NeedTFA", out var tfa) && tfa.ValueKind == JsonValueKind.Number && tfa.GetInt32() != 0)
            || String(data, "ticket") is not { } ticket
            || ticket.Contains("!tfa!", StringComparison.Ordinal))
        {
            throw new SessionException(
                SessionErrorCodes.AuthenticationFailed,
                $"The Proxmox account {_userId} uses two-factor authentication, which NKeeper can't answer. Use an API token (user@realm!token, with its secret as the password) instead.");
        }

        _ticket = ticket;
        _csrf = String(data, "CSRFPreventionToken");
    }

    /// <summary>The cluster's VM or container with this ID, if the account can see it.</summary>
    public async Task<ProxmoxGuest?> FindGuestAsync(int vmId, CancellationToken cancellationToken)
    {
        var data = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api2/json/cluster/resources?type=vm"), cancellationToken);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (Number(item, "vmid") == vmId && String(item, "type") is "qemu" or "lxc" && String(item, "node") is { } node && NodeName().IsMatch(node))
            {
                return new ProxmoxGuest(vmId, String(item, "type")!, node, String(item, "name") ?? $"VM {vmId}", String(item, "status") ?? "unknown");
            }
        }

        return null;
    }

    /// <summary>Asks the guest's node to start a VNC proxy for one WebSocket connection (it waits about 10 seconds for it).</summary>
    public async Task<VncProxyTicket> CreateVncProxyAsync(ProxmoxGuest guest, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GuestPath(guest)}/vncproxy")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["websocket"] = "1" }),
        };
        var data = await SendAsync(request, cancellationToken);
        var port = data.TryGetProperty("port", out var p) ? p.ValueKind == JsonValueKind.Number ? p.GetInt32().ToString(CultureInfo.InvariantCulture) : p.GetString() : null;
        return port is { Length: > 0 } && String(data, "ticket") is { } ticket
            ? new VncProxyTicket(port, ticket)
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, "Proxmox didn't open the console.");
    }

    /// <summary>Connects to the console's WebSocket, which carries the VNC connection (RFB).</summary>
    public async Task<WebSocket> ConnectVncAsync(ProxmoxGuest guest, VncProxyTicket proxy, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(new Uri(_baseAddress, $"{GuestPath(guest)}/vncwebsocket"))
        {
            Scheme = "wss",
            Query = $"port={Uri.EscapeDataString(proxy.Port)}&vncticket={Uri.EscapeDataString(proxy.Ticket)}",
        }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("binary");
        Authorize(socket.Options.SetRequestHeader);
        try
        {
            using var invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
            await socket.ConnectAsync(uri, invoker, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private static string GuestPath(ProxmoxGuest guest) =>
        $"api2/json/nodes/{Uri.EscapeDataString(guest.Node)}/{guest.Type}/{guest.VmId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Adds the API token, or the ticket cookie (and CSRF token, which writes need).</summary>
    private void Authorize(Action<string, string?> set)
    {
        if (_token is not null)
        {
            set("Authorization", _token);
        }
        else if (_ticket is not null)
        {
            set("Cookie", $"PVEAuthCookie={_ticket}");
            if (_csrf is not null)
            {
                set("CSRFPreventionToken", _csrf);
            }
        }
    }

    /// <summary>Sends a request and returns its <c>data</c>.</summary>
    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool signingIn = false)
    {
        using (request)
        {
            if (!signingIn)
            {
                Authorize((name, value) => request.Headers.TryAddWithoutValidation(name, value));
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SessionException(SessionErrorCodes.Timeout, $"Proxmox at {_display} didn't answer in time.");
            }
            catch (HttpRequestException ex)
            {
                throw new SessionException(
                    SessionErrorCodes.ConnectionFailed,
                    ex.InnerException is System.Security.Authentication.AuthenticationException
                        ? $"The certificate of {_display} didn't match the one trusted in NKeeper, so the connection was refused."
                        : ex.InnerException is SocketException socket
                            ? $"Couldn't connect to Proxmox at {_display} ({SocketErrors.Describe(socket.SocketErrorCode, "HTTPS")})."
                            : $"Couldn't connect to Proxmox at {_display}.");
            }

            using (response)
            {
                // Proxmox puts its error message in the status line.
                var reason = response.ReasonPhrase is { Length: > 0 } r ? r : null;
                if (response.StatusCode == HttpStatusCode.Unauthorized || (signingIn && !response.IsSuccessStatusCode))
                {
                    throw new SessionException(
                        SessionErrorCodes.AuthenticationFailed,
                        _token is not null
                            ? $"Proxmox refused the API token {_userId}."
                            : $"Proxmox refused the login {_userId}. Check the username, password and realm (user@pam or user@pve).");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new SessionException(
                        SessionErrorCodes.ConnectionFailed,
                        $"The Proxmox account {_userId} isn't allowed to do this{(reason is null ? "" : $" ({reason})")}. Opening a console needs the VM.Console permission on the VM.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Proxmox refused the request ({(int)response.StatusCode}{(reason is null ? "" : $": {reason}")}).");
                }

                try
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    return body.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
                }
                catch (JsonException)
                {
                    throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The answer from {_display} wasn't Proxmox's API. Is this a Proxmox VE node (port 8006)?");
                }
            }
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})$")]
    private static partial Regex NodeName();
}
