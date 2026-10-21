using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Kvm;
using NKeeper.Api.Proxmox;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

/// <summary>A Proxmox API request: the node's Proxmox service and login, and optionally a certificate to trust.</summary>
public record ProxmoxRequest : SessionStartMessage
{
    /// <summary>The node's certificate fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

/// <summary>A request about one VM or container of the cluster.</summary>
public record ProxmoxGuestRequest : ProxmoxRequest
{
    /// <summary>The Proxmox VM ID of the VM or container.</summary>
    [Range(1, 999_999_999, ErrorMessage = "Choose a virtual machine.")]
    public int Vm { get; init; }
}

public sealed record ProxmoxConsoleRequest : ProxmoxGuestRequest;

public sealed record ProxmoxActionRequest : ProxmoxGuestRequest
{
    /// <summary>One of <see cref="ProxmoxActions"/>.</summary>
    [Required(ErrorMessage = "Choose an action.")]
    [AllowedValues(
        ProxmoxActions.Start, ProxmoxActions.Shutdown, ProxmoxActions.Reboot, ProxmoxActions.Pause, ProxmoxActions.Hibernate,
        ProxmoxActions.Resume, ProxmoxActions.Stop, ProxmoxActions.Reset, ErrorMessage = "Choose an action.")]
    public string Action { get; init; } = string.Empty;
}

public sealed record ProxmoxMigrateRequest : ProxmoxGuestRequest
{
    /// <summary>The node to move the guest to.</summary>
    [Required(ErrorMessage = "Choose a node.")]
    [StringLength(63)]
    public string Target { get; init; } = string.Empty;

    /// <summary>Move a running VM live (a running container is restarted on the other node).</summary>
    public bool Online { get; init; }

    /// <summary>VMs: copy disks on the node's local storage too.</summary>
    public bool WithLocalDisks { get; init; }
}

public sealed record ProxmoxTaskRequest : ProxmoxRequest
{
    /// <summary>The task's ID (UPID), as an action or migration returned it.</summary>
    [Required]
    [StringLength(256)]
    public string Upid { get; init; } = string.Empty;
}

/// <summary>Either the console is open (connect noVNC to /api/vnc/channel/{SessionId} with <see cref="Password"/>), or the node's certificate needs to be trusted first.</summary>
public sealed record ProxmoxConsoleResponse(string Status, string? SessionId, string? Password, string? Target, WebCertificateResponse? Certificate);

/// <summary>A VM or container: <see cref="Type"/> is "qemu" or "lxc", <see cref="Status"/> Proxmox's ("running", "stopped"…).</summary>
public sealed record ProxmoxGuestInfo(int VmId, string Type, string Node, string Name, string Status);

/// <summary>
/// The answer to the task requests, or "trustRequired" with the node's certificate: a guest (with
/// the cluster's nodes, for migrations), a started task's ID, or a task's status.
/// </summary>
public sealed record ProxmoxTaskResponse(
    string Status,
    string? Target,
    WebCertificateResponse? Certificate,
    ProxmoxGuestInfo? Guest = null,
    IReadOnlyList<ProxmoxNode>? Nodes = null,
    string? Upid = null,
    ProxmoxTaskStatus? Task = null);

/// <summary>
/// Proxmox VE VMs and containers through the Proxmox API, with a Proxmox login: their consoles,
/// as Proxmox's own web console opens them (sign in, start a VNC proxy for the guest with
/// <c>vncproxy</c>, and relay its WebSocket, <c>vncwebsocket</c>, to NKeeper's VNC console), and
/// their power actions and migrations. Any node of the cluster serves every guest. The browser
/// never contacts Proxmox.
/// </summary>
[ApiController]
[Authorize]
public sealed class ProxmoxController(ConsoleSessionStore consoles) : ControllerBase
{
    [HttpPost("api/proxmox/consoles")]
    [ProducesResponseType<ProxmoxConsoleResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProxmoxConsoleResponse>> OpenConsole(ProxmoxConsoleRequest request, CancellationToken cancellationToken)
    {
        ConsoleEndpoint? endpoint = null;
        try
        {
            var opened = await OpenAsync(request, cancellationToken);
            if (opened.Certificate is { } certificate)
            {
                return new ProxmoxConsoleResponse("trustRequired", null, null, opened.Display, certificate);
            }

            using var connection = opened.Connection!;
            var guest = await FindRunningGuestAsync(connection, request.Vm, cancellationToken);
            var proxy = await connection.Api.CreateVncProxyAsync(guest, cancellationToken);
            endpoint = connection.ToEndpoint(guest, proxy);
            var session = consoles.TryCreate(User.GetUserId(), connection.Target.HostId, guest.Name, ConsoleProtocols.Vnc, proxy.Ticket, endpoint);
            if (session is null)
            {
                throw new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {ConsoleSessionStore.MaxSessionsPerUser} open VM consoles. Close one and try again.");
            }

            endpoint = null; // Owned by the session now.
            return new ProxmoxConsoleResponse("ready", session.Id, session.Password, opened.Display, null);
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
        finally
        {
            endpoint?.Dispose();
        }
    }

    /// <summary>A VM or container's state, and the cluster's nodes (to migrate to).</summary>
    [HttpPost("api/proxmox/guest")]
    [ProducesResponseType<ProxmoxTaskResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ProxmoxTaskResponse>> Guest(ProxmoxGuestRequest request, CancellationToken cancellationToken) =>
        WithApiAsync(request, async (connection, display) =>
        {
            var guest = await FindGuestAsync(connection, request.Vm, cancellationToken);
            var nodes = await connection.Api.GetNodesAsync(cancellationToken);
            return new ProxmoxTaskResponse("ready", display, null, Info(guest), nodes);
        }, cancellationToken);

    /// <summary>Starts a power action (start, shut down, reboot, pause, hibernate, resume, stop or reset); answers with its task's ID.</summary>
    [HttpPost("api/proxmox/power")]
    [ProducesResponseType<ProxmoxTaskResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ProxmoxTaskResponse>> Power(ProxmoxActionRequest request, CancellationToken cancellationToken) =>
        WithApiAsync(request, async (connection, display) =>
        {
            var guest = await FindGuestAsync(connection, request.Vm, cancellationToken);
            var upid = await connection.Api.StartActionAsync(guest, request.Action, cancellationToken);
            await Audit.RecordAsync(new Auditing.AuditEntry("proxmox.power", connection.Target.HostId, $"{request.Action} · {GuestDisplay(guest)}"));
            return new ProxmoxTaskResponse("ready", display, null, Info(guest), Upid: upid);
        }, cancellationToken);

    /// <summary>Starts moving a VM or container to another node; answers with the migration task's ID.</summary>
    [HttpPost("api/proxmox/migrate")]
    [ProducesResponseType<ProxmoxTaskResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ProxmoxTaskResponse>> Migrate(ProxmoxMigrateRequest request, CancellationToken cancellationToken) =>
        WithApiAsync(request, async (connection, display) =>
        {
            var guest = await FindGuestAsync(connection, request.Vm, cancellationToken);
            var nodes = await connection.Api.GetNodesAsync(cancellationToken);
            var target = nodes.FirstOrDefault(n => string.Equals(n.Name, request.Target, StringComparison.OrdinalIgnoreCase))
                ?? throw new SessionException(SessionErrorCodes.BadRequest, $"There's no node “{request.Target}” in this Proxmox cluster.");
            if (string.Equals(target.Name, guest.Node, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionException(SessionErrorCodes.BadRequest, $"“{guest.Name}” is already on {guest.Node}.");
            }

            if (!target.Online)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, $"Node {target.Name} is offline.");
            }

            var upid = await connection.Api.MigrateAsync(guest, target.Name, request.Online && guest.Status == "running", request.WithLocalDisks, cancellationToken);
            await Audit.RecordAsync(new Auditing.AuditEntry("proxmox.migrate", connection.Target.HostId, $"{GuestDisplay(guest)} · {guest.Node} → {target.Name}"));
            return new ProxmoxTaskResponse("ready", display, null, Info(guest), Upid: upid);
        }, cancellationToken);

    /// <summary>Whether a task is still running, and how it ended.</summary>
    [HttpPost("api/proxmox/task")]
    [ProducesResponseType<ProxmoxTaskResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<ProxmoxTaskResponse>> TaskStatus(ProxmoxTaskRequest request, CancellationToken cancellationToken) =>
        WithApiAsync(request, async (connection, display) =>
            new ProxmoxTaskResponse("ready", display, null, Task: await connection.Api.GetTaskStatusAsync(request.Upid, cancellationToken)), cancellationToken);

    private async Task<ActionResult<ProxmoxTaskResponse>> WithApiAsync(
        ProxmoxRequest request, Func<ProxmoxConnection, string, Task<ProxmoxTaskResponse>> action, CancellationToken cancellationToken)
    {
        try
        {
            var opened = await OpenAsync(request, cancellationToken);
            if (opened.Certificate is { } certificate)
            {
                return new ProxmoxTaskResponse("trustRequired", opened.Display, certificate);
            }

            using var connection = opened.Connection!;
            return await action(connection, opened.Display);
        }
        catch (CertificateChangedException ex)
        {
            return this.CertificateChanged(ex);
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    /// <summary>Resolves the node's Proxmox service, checks its certificate and signs in; or answers with the certificate to trust.</summary>
    private Task<(ProxmoxConnection? Connection, string Display, WebCertificateResponse? Certificate)> OpenAsync(ProxmoxRequest request, CancellationToken cancellationToken) =>
        ConnectAsync(request, HttpContext.RequestServices, User, cancellationToken);

    /// <summary>The same, for others that use a node's API (migrations).</summary>
    internal static async Task<(ProxmoxConnection? Connection, string Display, WebCertificateResponse? Certificate)> ConnectAsync(
        ProxmoxRequest request, IServiceProvider services, System.Security.Claims.ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var target = await services.GetRequiredService<SessionTargetResolver>()
            .ResolveAsync(request, RequestUser.From(user), ServiceProtocol.Proxmox, ProxmoxApi.DefaultPort, cancellationToken);
        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        var display = WebSessionsController.TargetUri(https: true, target.Address, target.Port).ToString();
        var tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, target.Port, cancellationToken);
        ProxmoxApi? api = null;
        try
        {
            var trust = await CertificateTrust.CheckAsync(
                target.HostId, target.Address, target.Port, target.KeyProtocol, target.KnownKeys, request.TrustFingerprint, user.GetUserId(),
                services.GetRequiredService<TrustedKeyStore>(), cancellationToken, tunnel);
            if (trust.Prompt is { } certificate)
            {
                tunnel.Dispose();
                return (null, display, new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
            }

            api = new ProxmoxApi(target, trust.PinnedFingerprint, tunnel);
            await api.SignInAsync(cancellationToken);
            return (new ProxmoxConnection(target, api, tunnel), display, null);
        }
        catch
        {
            api?.Dispose();
            tunnel.Dispose();
            throw;
        }
    }

    private static async Task<ProxmoxGuest> FindGuestAsync(ProxmoxConnection connection, int vm, CancellationToken cancellationToken) =>
        await connection.Api.FindGuestAsync(vm, cancellationToken)
        ?? throw new SessionException(
            SessionErrorCodes.HostNotFound,
            $"There's no VM or container {vm} in this Proxmox cluster that {ProxmoxApi.UserId(connection.Target.Username, connection.Target.Domain)} can see.");

    private static async Task<ProxmoxGuest> FindRunningGuestAsync(ProxmoxConnection connection, int vm, CancellationToken cancellationToken)
    {
        var guest = await FindGuestAsync(connection, vm, cancellationToken);
        return guest.Status == "running"
            ? guest
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, $"“{guest.Name}” is not running ({guest.Status}). Start it, then open its console.");
    }

    private static ProxmoxGuestInfo Info(ProxmoxGuest guest) => new(guest.VmId, guest.Type, guest.Node, guest.Name, guest.Status);

    private static string GuestDisplay(ProxmoxGuest guest) => $"{(guest.Type == "lxc" ? "CT" : "VM")} {guest.VmId} {guest.Name}";

    private Auditing.AuditLog Audit => HttpContext.RequestServices.GetRequiredService<Auditing.AuditLog>();

    /// <summary>A signed-in API connection to the node, through its jump-host tunnel (owned until handed to a console).</summary>
    internal sealed class ProxmoxConnection(SessionTarget target, ProxmoxApi api, JumpTunnel tunnel) : IDisposable
    {
        private bool _handedOver;

        public SessionTarget Target { get; } = target;

        public ProxmoxApi Api { get; } = api;

        /// <summary>Hands the API connection and tunnel to a console, which then owns them.</summary>
        public ProxmoxEndpoint ToEndpoint(ProxmoxGuest guest, VncProxyTicket proxy)
        {
            _handedOver = true;
            return new ProxmoxEndpoint(Api, tunnel, guest, proxy);
        }

        public void Dispose()
        {
            if (!_handedOver)
            {
                Api.Dispose();
                tunnel.Dispose();
            }
        }
    }
}
