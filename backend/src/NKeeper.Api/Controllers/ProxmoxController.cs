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

/// <summary>A Proxmox console request: the node's Proxmox service and login, the VM or container, and optionally a certificate to trust.</summary>
public sealed record ProxmoxConsoleRequest : SessionStartMessage
{
    /// <summary>The node's certificate fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>The Proxmox VM ID of the VM or container.</summary>
    [Range(1, 999_999_999, ErrorMessage = "Choose a virtual machine.")]
    public int Vm { get; init; }
}

/// <summary>Either the console is open (connect noVNC to /api/vnc/channel/{SessionId} with <see cref="Password"/>), or the node's certificate needs to be trusted first.</summary>
public sealed record ProxmoxConsoleResponse(string Status, string? SessionId, string? Password, string? Target, WebCertificateResponse? Certificate);

/// <summary>
/// Consoles of Proxmox VE VMs and containers through the Proxmox API, as Proxmox's own web console
/// opens them: sign in with a Proxmox login, start a VNC proxy for the guest
/// (<c>vncproxy</c>), and relay its WebSocket (<c>vncwebsocket</c>) to NKeeper's VNC console. Any
/// node of the cluster serves every guest. The browser never contacts Proxmox.
/// </summary>
[ApiController]
[Authorize]
public sealed class ProxmoxController(ConsoleSessionStore consoles) : ControllerBase
{
    [HttpPost("api/proxmox/consoles")]
    [ProducesResponseType<ProxmoxConsoleResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProxmoxConsoleResponse>> OpenConsole(ProxmoxConsoleRequest request, CancellationToken cancellationToken)
    {
        JumpTunnel? tunnel = null;
        ProxmoxApi? api = null;
        ConsoleEndpoint? endpoint = null;
        try
        {
            var services = HttpContext.RequestServices;
            var target = await services.GetRequiredService<SessionTargetResolver>()
                .ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Proxmox, ProxmoxApi.DefaultPort, cancellationToken);
            if (target.Password is null)
            {
                throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
            }

            var display = WebSessionsController.TargetUri(https: true, target.Address, target.Port).ToString();
            tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, target.Port, cancellationToken);
            var trust = await CertificateTrust.CheckAsync(
                target.HostId, target.Address, target.Port, target.KeyProtocol, target.KnownKeys, request.TrustFingerprint, User.GetUserId(),
                services.GetRequiredService<TrustedKeyStore>(), cancellationToken, tunnel);
            if (trust.Prompt is { } certificate)
            {
                return new ProxmoxConsoleResponse(
                    "trustRequired", null, null, display, new WebCertificateResponse(certificate.Fingerprint, certificate.Subject, certificate.Issuer, certificate.NotAfter));
            }

            api = new ProxmoxApi(target, trust.PinnedFingerprint, tunnel);
            await api.SignInAsync(cancellationToken);
            var guest = await api.FindGuestAsync(request.Vm, cancellationToken)
                ?? throw new SessionException(
                    SessionErrorCodes.HostNotFound, $"There's no VM or container {request.Vm} in this Proxmox cluster that {ProxmoxApi.UserId(target.Username, target.Domain)} can see.");
            if (guest.Status != "running")
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, $"“{guest.Name}” is not running ({guest.Status}). Start it, then open its console.");
            }

            var proxy = await api.CreateVncProxyAsync(guest, cancellationToken);
            endpoint = new ProxmoxEndpoint(api, tunnel, guest, proxy);
            (api, tunnel) = (null, null); // Owned by the endpoint now.
            var session = consoles.TryCreate(User.GetUserId(), target.HostId, guest.Name, ConsoleProtocols.Vnc, proxy.Ticket, endpoint);
            if (session is null)
            {
                throw new SessionException(
                    SessionErrorCodes.TooManySessions, $"You already have {ConsoleSessionStore.MaxSessionsPerUser} open VM consoles. Close one and try again.");
            }

            endpoint = null; // Owned by the session now.
            return new ProxmoxConsoleResponse("ready", session.Id, session.Password, display, null);
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
            api?.Dispose();
            tunnel?.Dispose();
        }
    }
}
