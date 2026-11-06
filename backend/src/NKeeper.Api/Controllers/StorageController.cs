using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Diagnostics;
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
        StorageActions.MultipathReload, StorageActions.MultipathFlush, StorageActions.MultipathRename,
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

    /// <summary>Multipath rename: the map's new name.</summary>
    [StringLength(128)]
    public string? NewName { get; init; }
}

/// <summary>The host's storage (after an action, as it is afterwards), or the SSH host key to trust first.</summary>
/// <param name="Message">What an action did, when there's more to say than the new state shows (e.g. what discovery found).</param>
public sealed record StorageResponse(string Status, StorageStatus? Storage, HostKeyPromptResponse? HostKey, string? Message = null);

/// <summary>
/// A Linux host's shared storage over SSH: its iSCSI sessions and targets, multipath maps and DRBD
/// resources, and the everyday actions on them (iSCSI discovery, login, logout and rescan; multipath
/// reload, flush and rename; DRBD up, down, primary, secondary, connect, disconnect and adjust). Actions
/// are audited. NKeeper runs the host's own tools as root (or with sudo without a password).
/// </summary>
[ApiController]
[Authorize]
[Route("api/storage")]
public sealed class StorageController(AuditLog audit, DiagnosticsLog diagnostics) : ControllerBase
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
            var newName = request.NewName?.Trim();
            var command = StorageHost.CommandFor(request.Action, request.Target?.Trim(), request.Portal, request.Name, before, newName);
            var output = await StorageHost.RunActionAsync(client, command, cancellationToken);
            await audit.RecordAsync(new AuditEntry("storage.action", request.HostId, command.Description));

            // Success is judged by the host's state afterwards, not by the tool's exit code.
            var after = await StorageHost.GetStatusAsync(client, cancellationToken);
            var target = request.Target?.Trim();
            if (StorageHost.Unachieved(request.Action, target, request.Portal, request.Name, after, newName) is { } problem)
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{command.Description} didn't take effect: {problem}. {Printed(output)}");
            }

            return new StorageResponse(
                "ready", after, null, Message(request.Action, output, before, after) ?? StorageHost.Achieved(request.Action, target, request.Portal, request.Name, after, newName));
        }, cancellationToken);

    /// <summary>What the tool printed, for an action that didn't take effect.</summary>
    private static string Printed(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToList();
        return lines.Count == 0 ? "The tool printed nothing and reported success." : $"The tool printed: “{string.Join(" ", lines)}”";
    }

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
            if (request is StorageActionRequest failed && ex.Code is SessionErrorCodes.ConnectionFailed or SessionErrorCodes.Timeout)
            {
                RecordFailure(failed, ex);
            }

            return this.SessionFailure(ex);
        }
    }

    /// <summary>Why the host refused an action, for the diagnostics file; what it acted on becomes tokens, as they can name servers.</summary>
    private void RecordFailure(StorageActionRequest request, SessionException ex)
    {
        if (!diagnostics.Enabled)
        {
            return;
        }

        var message = ex.Message;
        foreach (var value in new[] { request.Name, request.NewName?.Trim(), request.Target?.Trim(), request.Portal }.Where(v => !string.IsNullOrEmpty(v)))
        {
            message = message.Replace(value!, diagnostics.Redactor.Token("name", value!), StringComparison.Ordinal);
        }

        diagnostics.RecordError("storage", "Warning", diagnostics.Redactor.Redact(message), null, null, new { action = request.Action });
    }
}
