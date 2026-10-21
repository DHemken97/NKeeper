using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Migration;
using NKeeper.Api.Proxmox;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Controllers;

/// <summary>A login to one host for a migration: its service (SSH, KVM or Proxmox) and credential, and a key or certificate the user chose to trust.</summary>
public sealed record HostLogin : SessionStartMessage
{
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

/// <summary>Which VM moves where, with which logins, and how.</summary>
public sealed record MigrationRequest
{
    /// <summary>"proxmox" (through the cluster's API) or "kvm" (libvirt over SSH).</summary>
    [Required]
    [AllowedValues("proxmox", "kvm", ErrorMessage = "Choose Proxmox or KVM.")]
    public string Via { get; init; } = "";

    /// <summary>The NKeeper host of the VM (for the audit log).</summary>
    public Guid VmHostId { get; init; }

    /// <summary>The Proxmox VM ID, or the libvirt UUID.</summary>
    [Required]
    [StringLength(64)]
    public string Vm { get; init; } = "";

    /// <summary>Proxmox: the node's Proxmox service. KVM: the old host's KVM service.</summary>
    [Required]
    public HostLogin Source { get; init; } = null!;

    /// <summary>Proxmox: the node to move to.</summary>
    [StringLength(63)]
    public string? TargetNode { get; init; }

    /// <summary>KVM: the new host's KVM service. Proxmox: an SSH (or KVM) login to the node it moves to, to check and move its storage.</summary>
    public HostLogin? Target { get; init; }

    /// <summary>Proxmox: an SSH (or KVM) login to the node the VM is on now.</summary>
    public HostLogin? SourceShell { get; init; }

    public MigrationOptions Options { get; init; } = new();
}

/// <summary>
/// The plan, a started migration (<see cref="JobId"/>), or which login's identity must be trusted
/// first: <see cref="Login"/> is "source", "target" or "sourceShell", with an SSH host key or a
/// certificate.
/// </summary>
public sealed record MigrationResponse(
    string Status, string? Login = null, HostKeyPromptResponse? HostKey = null, WebCertificateResponse? Certificate = null, MigrationPlan? Plan = null, string? JobId = null);

/// <summary>
/// Migrations that move a VM's storage with it: plan (read both hosts and show what would happen),
/// start (a job on the server that rolls back if a step fails), and follow.
/// </summary>
[ApiController]
[Authorize]
[Route("api/migrations")]
public sealed class MigrationsController(MigrationJobs jobs) : ControllerBase
{
    [HttpPost("plan")]
    [ProducesResponseType<MigrationResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<MigrationResponse>> Plan(MigrationRequest request, CancellationToken cancellationToken) =>
        WithMoverAsync(request, async mover =>
        {
            using (mover)
            {
                return new MigrationResponse("ready", Plan: MigrationPlanner.Plan(await mover.InspectAsync(cancellationToken), request.Options));
            }
        }, cancellationToken);

    /// <summary>Plans again (the hosts may have changed), then starts the migration if nothing blocks it.</summary>
    [HttpPost]
    [ProducesResponseType<MigrationResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<MigrationResponse>> Start(MigrationRequest request, CancellationToken cancellationToken) =>
        WithMoverAsync(request, async mover =>
        {
            try
            {
                var plan = MigrationPlanner.Plan(await mover.InspectAsync(cancellationToken), request.Options);
                if (plan.Blockers.Count > 0)
                {
                    throw new SessionException(SessionErrorCodes.BadRequest, plan.Blockers[0]);
                }

                var job = jobs.Start(User.GetUserId(), request.VmHostId == Guid.Empty ? request.Source.HostId : request.VmHostId, $"{request.Via}:{request.Vm}", mover, plan)
                    ?? throw new SessionException(SessionErrorCodes.BadRequest, $"“{mover.GuestName}” is already being moved.");
                mover = null!; // The job owns it now.
                return new MigrationResponse("started", Plan: plan, JobId: job.Id);
            }
            finally
            {
                mover?.Dispose();
            }
        }, cancellationToken);

    [HttpGet("{id}")]
    [ProducesResponseType<MigrationJobView>(StatusCodes.Status200OK)]
    public ActionResult<MigrationJobView> Get(string id) => jobs.Find(id, User.GetUserId()) is { } job ? job.View() : NotFound();

    private async Task<ActionResult<MigrationResponse>> WithMoverAsync(MigrationRequest request, Func<GuestMover, Task<MigrationResponse>> action, CancellationToken cancellationToken)
    {
        var opened = new List<IDisposable>();
        try
        {
            var mover = request.Via == "proxmox"
                ? await ProxmoxMoverAsync(request, opened, cancellationToken)
                : await LibvirtMoverAsync(request, opened, cancellationToken);
            opened.Clear(); // The mover owns them now.
            return await action(mover);
        }
        catch (TrustNeeded trust)
        {
            return trust.Response;
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
            opened.ForEach(o => o.Dispose());
        }
    }

    private async Task<GuestMover> ProxmoxMoverAsync(MigrationRequest request, List<IDisposable> opened, CancellationToken cancellationToken)
    {
        if (!int.TryParse(request.Vm, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var vmId) || request.TargetNode is not { Length: > 0 } targetNode)
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "Choose a VM and the node to move it to.");
        }

        var source = request.Source;
        var connected = await ProxmoxController.ConnectAsync(
            new ProxmoxRequest
            {
                HostId = source.HostId, ServiceIndex = source.ServiceIndex, CredentialId = source.CredentialId,
                Username = source.Username, Password = source.Password, TrustFingerprint = source.TrustFingerprint,
            },
            HttpContext.RequestServices,
            User,
            cancellationToken);
        if (connected.Certificate is { } certificate)
        {
            throw new TrustNeeded(new MigrationResponse("trustRequired", "source", Certificate: certificate));
        }

        var connection = connected.Connection!;
        opened.Add(connection);
        var guest = await connection.Api.FindGuestAsync(vmId, cancellationToken)
            ?? throw new SessionException(SessionErrorCodes.HostNotFound, $"There's no VM or container {vmId} in this Proxmox cluster that {ProxmoxApi.UserId(connection.Target.Username, connection.Target.Domain)} can see.");
        var sourceShell = request.SourceShell is { } s ? await ShellAsync(s, "sourceShell", opened, cancellationToken) : null;
        var targetShell = request.Target is { } t ? await ShellAsync(t, "target", opened, cancellationToken) : null;
        return new ProxmoxMover(connection, guest, targetNode, sourceShell, targetShell);
    }

    private async Task<GuestMover> LibvirtMoverAsync(MigrationRequest request, List<IDisposable> opened, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Vm, out var vm) || request.Target is null)
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "Choose a VM and the host to move it to.");
        }

        var (sourceShell, sourceTarget) = await ShellWithTargetAsync(request.Source, "source", opened, cancellationToken);
        var (targetShell, targetTarget) = await ShellWithTargetAsync(request.Target, "target", opened, cancellationToken);
        var listing = await Kvm.KvmHost.ListAsync(sourceShell, cancellationToken);
        if (listing.ProxmoxNode is not null)
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "This host is a Proxmox node: move its VMs with Tasks ▸ Proxmox ▸ Migrate.");
        }

        var machine = listing.Vms.FirstOrDefault(v => string.Equals(v.Id, vm.ToString("D"), StringComparison.OrdinalIgnoreCase))
            ?? throw new SessionException(SessionErrorCodes.HostNotFound, "This virtual machine isn't on this host (any more). Scan the hypervisors to update where it runs.");
        return new LibvirtMover(
            vm, machine.Name, sourceShell, targetShell, await NameAsync(request.Source.HostId), await NameAsync(request.Target.HostId),
            sourceTarget.Address, targetTarget.Address, request.Source.HostId == request.Target.HostId);
    }

    private async Task<string> NameAsync(Guid hostId)
    {
        var db = HttpContext.RequestServices.GetRequiredService<NKeeper.Infrastructure.Persistence.NKeeperDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.Hosts.Where(h => h.Id == hostId).Select(h => h.Name)) ?? "the host";
    }

    private async Task<SshClient> ShellAsync(HostLogin login, string role, List<IDisposable> opened, CancellationToken cancellationToken) =>
        (await ShellWithTargetAsync(login, role, opened, cancellationToken)).Client;

    /// <summary>An SSH login through the host's SSH service, or its KVM service (which is SSH too).</summary>
    private async Task<(SshClient Client, SessionTarget Target)> ShellWithTargetAsync(HostLogin login, string role, List<IDisposable> opened, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var resolver = services.GetRequiredService<SessionTargetResolver>();
        SessionTarget target;
        try
        {
            target = await resolver.ResolveAsync(login, RequestUser.From(User), ServiceProtocol.Ssh, 22, cancellationToken);
        }
        catch (SessionException ex) when (ex.Code == SessionErrorCodes.WrongService)
        {
            target = await resolver.ResolveAsync(login, RequestUser.From(User), ServiceProtocol.Kvm, 22, cancellationToken);
        }

        try
        {
            var client = await SshCommandConnection.ConnectAsync(target, login.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
            opened.Add(client);
            return (client, target);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            throw new TrustNeeded(new MigrationResponse("trustRequired", role, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint)));
        }
    }

    /// <summary>A login's identity isn't trusted yet: answered with what to trust, and which login it is.</summary>
    private sealed class TrustNeeded(MigrationResponse response) : Exception("Trust required.")
    {
        public MigrationResponse Response { get; } = response;
    }
}
