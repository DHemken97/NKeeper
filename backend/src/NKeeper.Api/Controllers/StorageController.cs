using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Api.Storage;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Controllers;

/// <summary>A storage request: which host, SSH service and login, and optionally a host key to trust.</summary>
public record StorageRequest : SessionStartMessage
{
    /// <summary>The SSH host key fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

public sealed record StorageActionRequest : StorageRequest
{
    /// <summary>One of <see cref="StorageActions"/>.</summary>
    [Required(ErrorMessage = "Choose an action.")]
    [AllowedValues(
        StorageActions.IscsiDiscover, StorageActions.IscsiLogin, StorageActions.IscsiLogout, StorageActions.IscsiRescan,
        StorageActions.MultipathReload, StorageActions.MultipathFlush,
        StorageActions.DrbdUp, StorageActions.DrbdDown, StorageActions.DrbdPrimary, StorageActions.DrbdSecondary,
        StorageActions.DrbdConnect, StorageActions.DrbdDisconnect, StorageActions.DrbdAdjust, ErrorMessage = "Choose an action.")]
    public string Action { get; init; } = string.Empty;

    /// <summary>iSCSI login and logout: the target's name (IQN).</summary>
    [StringLength(223)]
    public string? Target { get; init; }

    /// <summary>iSCSI: the portal, e.g. <c>10.0.0.5:3260</c>.</summary>
    [StringLength(260)]
    public string? Portal { get; init; }

    /// <summary>A multipath map or DRBD resource.</summary>
    [StringLength(128)]
    public string? Name { get; init; }
}

/// <summary>The host's storage (after an action, as it is afterwards), or the SSH host key to trust first.</summary>
/// <param name="Message">What an action did, when there's more to say than the new state shows (e.g. what discovery found).</param>
public sealed record StorageResponse(string Status, StorageStatus? Storage, HostKeyPromptResponse? HostKey, string? Message = null);

/// <summary>
/// A Linux host's shared storage over SSH: its iSCSI sessions and targets, multipath maps and DRBD
/// resources, and the everyday actions on them (iSCSI discovery, login, logout and rescan; multipath
/// reload and flush; DRBD up, down, primary, secondary, connect, disconnect and adjust). Actions
/// are audited. NKeeper runs the host's own tools as root (or with sudo without a password).
/// </summary>
[ApiController]
[Authorize]
[Route("api/storage")]
public sealed class StorageController(AuditLog audit) : ControllerBase
{
    private const int DefaultSshPort = 22;

    [HttpPost("status")]
    [ProducesResponseType<StorageResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<StorageResponse>> Status(StorageRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client => new StorageResponse("ready", await StorageHost.GetStatusAsync(client, cancellationToken), null), cancellationToken);

    /// <summary>Runs an action on what the host has now, then answers with its storage afterwards.</summary>
    [HttpPost("action")]
    [ProducesResponseType<StorageResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<StorageResponse>> Action(StorageActionRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client =>
        {
            var before = await StorageHost.GetStatusAsync(client, cancellationToken);
            var command = StorageHost.CommandFor(request.Action, request.Target?.Trim(), request.Portal, request.Name, before);
            var output = await StorageHost.RunActionAsync(client, command, cancellationToken);
            await audit.RecordAsync(new AuditEntry("storage.action", request.HostId, command.Description));

            var after = await StorageHost.GetStatusAsync(client, cancellationToken);
            return new StorageResponse("ready", after, null, Message(request.Action, output, before, after));
        }, cancellationToken);

    /// <summary>Discovery's targets aren't obvious from the new state, so it says how many it found.</summary>
    private static string? Message(string action, string output, StorageStatus before, StorageStatus after)
    {
        if (action != StorageActions.IscsiDiscover)
        {
            return null;
        }

        var found = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var added = (after.Iscsi?.Nodes.Count ?? 0) - (before.Iscsi?.Nodes.Count ?? 0);
        return found == 0
            ? "The portal answered, but offers no targets to this initiator."
            : $"Found {found} {(found == 1 ? "target" : "targets")}{(added > 0 ? $", {added} new" : "")}.";
    }

    private async Task<ActionResult<StorageResponse>> WithHostAsync(StorageRequest request, Func<SshClient, Task<StorageResponse>> action, CancellationToken cancellationToken)
    {
        try
        {
            var services = HttpContext.RequestServices;
            var target = await services.GetRequiredService<SessionTargetResolver>()
                .ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            using var client = await SshCommandConnection.ConnectAsync(
                target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
            return await action(client);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new StorageResponse("trustRequired", null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }
}
