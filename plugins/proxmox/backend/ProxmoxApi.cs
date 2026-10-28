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
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Web;
using NKeeper.Sdk.Web;

namespace NKeeper.Api.Proxmox;

/// <summary>A VM (qemu) or container (lxc) of a Proxmox cluster, as the API lists it.</summary>
internal sealed record ProxmoxGuest(int VmId, string Type, string Node, string Name, string Status);

/// <summary>A console the API opened: connect to its WebSocket with <see cref="Ticket"/>, which is also the VNC password.</summary>
internal sealed record VncProxyTicket(string Port, string Ticket);

/// <summary>A node of the cluster, and whether it is online.</summary>
public sealed record ProxmoxNode(string Name, bool Online);

/// <summary>A Proxmox task (e.g. a shutdown or a migration): still running, or its exit status ("OK", or what went wrong).</summary>
/// <param name="Already">Set when the task "failed" only because the guest already was as it would leave it (e.g. "was already running"); <see cref="ExitStatus"/> is then "OK".</param>
public sealed record ProxmoxTaskStatus(bool Running, string? ExitStatus, string? Already = null);

/// <summary>The power actions NKeeper offers, and the guest status call each one is.</summary>
public static partial class ProxmoxActions
{
    public const string Start = "start";
    public const string Shutdown = "shutdown";
    public const string Reboot = "reboot";
    public const string Pause = "pause";
    public const string Hibernate = "hibernate";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string Reset = "reset";

    /// <summary>The status call (<c>status/{call}</c>) and extra parameters for an action, or null when the guest type can't do it.</summary>
    internal static (string Call, Dictionary<string, string> Parameters)? For(string action, string type) => (action, type) switch
    {
        (Start or Shutdown or Reboot or Resume or Stop, _) => (action, []),
        (Pause, _) => ("suspend", []),
        // Hibernating saves the VM's memory to disk and stops it; starting it resumes it.
        (Hibernate, "qemu") => ("suspend", new() { ["todisk"] = "1" }),
        (Reset, "qemu") => ("reset", []),
        _ => null,
    };

    /// <summary>
    /// What to say when a guest with this status already is as the action would leave it (started:
    /// running; shut down or stopped: off), so there's nothing to do; null otherwise.
    /// </summary>
    internal static string? Already(string action, string status) => (action, status) switch
    {
        (Start, "running") => "was already running",
        (Shutdown or Stop, "stopped") => "was already off",
        _ => null,
    };

    /// <summary>
    /// The same, from Proxmox's complaint, e.g. "VM 101 already running" to a start or "CT 300 not
    /// running" to a shutdown: the state wanted, which Proxmox reports as a failure.
    /// </summary>
    internal static string? AlreadyFromMessage(string action, string? message) =>
        message is not null && AlreadyMessage().Match(message) is { Success: true } match
            ? Already(action, match.Groups["state"].Value == "already running" ? "running" : "stopped")
            : null;

    /// <summary>The action a task type is (<c>qmstart</c>, <c>vzshutdown</c>…), if it's start, shutdown or stop.</summary>
    internal static string? FromTaskType(string type) => (type.StartsWith("qm", StringComparison.Ordinal) || type.StartsWith("vz", StringComparison.Ordinal) ? type[2..] : type) switch
    {
        Start => Start,
        Shutdown => Shutdown,
        Stop => Stop,
        _ => null,
    };

    [GeneratedRegex(@"\b(?:VM|CT) \d+ (?<state>already running|not running)\b")]
    private static partial Regex AlreadyMessage();
}

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

    /// <summary>The cluster's nodes.</summary>
    public async Task<List<ProxmoxNode>> GetNodesAsync(CancellationToken cancellationToken)
    {
        var data = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api2/json/nodes"), cancellationToken);
        return data.ValueKind != JsonValueKind.Array
            ? []
            : [.. data.EnumerateArray()
                .Select(n => (Name: String(n, "node"), Status: String(n, "status")))
                .Where(n => n.Name is not null && NodeName().IsMatch(n.Name))
                .Select(n => new ProxmoxNode(n.Name!, n.Status == "online"))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Starts a power action (one of <see cref="ProxmoxActions"/>) on the guest; returns its task's ID
    /// (UPID), or no task and what to say when the guest already is as the action would leave it.
    /// </summary>
    public async Task<(string? Upid, string? Already)> StartActionAsync(ProxmoxGuest guest, string action, CancellationToken cancellationToken)
    {
        var (call, parameters) = ProxmoxActions.For(action, guest.Type)
            ?? throw new SessionException(SessionErrorCodes.BadRequest, $"Containers can't {(action == ProxmoxActions.Hibernate ? "hibernate" : "be reset")}. Stop or reboot it instead.");
        if (ProxmoxActions.Already(action, guest.Status) is { } already)
        {
            return (null, already);
        }

        try
        {
            return (await StartTaskAsync($"{GuestPath(guest)}/status/{call}", parameters, cancellationToken), null);
        }
        catch (SessionException ex) when (ProxmoxActions.AlreadyFromMessage(action, ex.Message) is { } said)
        {
            // It changed since it was listed. (Proxmox refuses containers at once; VMs in the task, see GetTaskStatusAsync.)
            return (null, said);
        }
    }

    /// <summary>
    /// Moves the guest to another node; returns the migration task's ID. A running VM moves live
    /// (<paramref name="online"/>); a running container is restarted on the other node.
    /// </summary>
    public Task<string> MigrateAsync(ProxmoxGuest guest, string target, bool online, bool withLocalDisks, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string> { ["target"] = target };
        if (online)
        {
            parameters[guest.Type == "lxc" ? "restart" : "online"] = "1";
        }

        if (withLocalDisks && guest.Type == "qemu")
        {
            parameters["with-local-disks"] = "1";
        }

        return StartTaskAsync($"{GuestPath(guest)}/migrate", parameters, cancellationToken);
    }

    /// <summary>A task's status, from the node that runs it (named in its UPID).</summary>
    public async Task<ProxmoxTaskStatus> GetTaskStatusAsync(string upid, CancellationToken cancellationToken)
    {
        var node = TaskNode(upid) ?? throw new SessionException(SessionErrorCodes.BadRequest, "That isn't a Proxmox task.");
        var data = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api2/json/nodes/{Uri.EscapeDataString(node)}/tasks/{Uri.EscapeDataString(upid)}/status"), cancellationToken);
        var running = String(data, "status") != "stopped";
        var exit = String(data, "exitstatus");
        return !running && exit != "OK" && TaskAction(upid) is { } action && ProxmoxActions.AlreadyFromMessage(action, exit) is { } already
            ? new ProxmoxTaskStatus(false, "OK", already)
            : new ProxmoxTaskStatus(running, exit);
    }

    /// <summary>The power action a task is (its ID's type, e.g. <c>UPID:pve1:…:qmstart:101:root@pam:</c>), if it's start, shutdown or stop.</summary>
    internal static string? TaskAction(string upid) =>
        upid.Split(':') is ["UPID", _, _, _, _, var type, ..] ? ProxmoxActions.FromTaskType(type) : null;

    /// <summary>The node in a task ID, e.g. <c>UPID:pve1:0000ABCD:…</c>; null when it isn't one.</summary>
    internal static string? TaskNode(string upid) =>
        upid.Split(':') is ["UPID", var node, ..] && NodeName().IsMatch(node) ? node : null;

    private async Task<string> StartTaskAsync(string path, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(parameters) };
        var data = await SendAsync(request, cancellationToken);
        return data.ValueKind == JsonValueKind.String && data.GetString() is { } upid && TaskNode(upid) is not null
            ? upid
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, "Proxmox didn't start the task.");
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
                        $"The Proxmox account {_userId} isn't allowed to do this{(reason is null ? "" : $" ({reason})")}. Consoles need the VM.Console permission on the VM, power actions VM.PowerMgmt, and migrations VM.Migrate.");
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
