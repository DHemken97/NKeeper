using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Selinux;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Controllers;

/// <summary>Which host, SSH service and login; optionally a host key to trust.</summary>
public record SelinuxRequest : SessionStartMessage
{
    /// <summary>The SSH host key fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

/// <summary>Reads the host's SELinux; denials from this time range.</summary>
public sealed record SelinuxStatusRequest : SelinuxRequest
{
    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }
}

public sealed record SelinuxGenerateRequest : SelinuxRequest
{
    [Required(ErrorMessage = "Name the policy.")]
    [StringLength(64)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The audit events to allow.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "Choose what to allow.")]
    [MaxLength(SelinuxHost.MaxEvents, ErrorMessage = "Choose fewer events.")]
    public IReadOnlyList<long> EventIds { get; init; } = [];
}

public sealed record SelinuxInstallRequest : SelinuxRequest
{
    /// <summary>The policy's source: a type enforcement (.te) file.</summary>
    [Required(ErrorMessage = "Enter the policy.")]
    [StringLength(256 * 1024)]
    public string Source { get; init; } = string.Empty;
}

public sealed record SelinuxRemoveRequest : SelinuxRequest
{
    [Required]
    [StringLength(128)]
    public string Name { get; init; } = string.Empty;

    public int Priority { get; init; }
}

/// <summary>The host's SELinux, a generated policy's source, or the SSH host key to trust first.</summary>
public sealed record SelinuxResponse(string Status, SelinuxStatus? Selinux, HostKeyPromptResponse? HostKey, string? Source = null, string? Message = null);

/// <summary>
/// A Linux host's SELinux over SSH: its mode, policy modules and denials in one place. Installing and
/// removing policies is audited, and judged by the modules the host lists afterwards. Modules from the
/// distribution can't be removed or replaced here, as the system depends on them.
/// </summary>
[ApiController]
[Authorize]
[Route("api/selinux")]
public sealed class SelinuxController(AuditLog audit) : ControllerBase
{
    private const int DefaultSshPort = 22;

    [HttpPost("status")]
    [ProducesResponseType<SelinuxResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SelinuxResponse>> Status(SelinuxStatusRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client =>
        {
            if (request.Since > request.Until)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "The start is after the stop.");
            }

            return new SelinuxResponse("ready", await SelinuxHost.GetStatusAsync(client, request.Since, request.Until, cancellationToken), null);
        }, cancellationToken);

    /// <summary>Writes a policy that allows these denials, without installing it: the user reads it first.</summary>
    [HttpPost("generate")]
    [ProducesResponseType<SelinuxResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SelinuxResponse>> Generate(SelinuxGenerateRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client =>
        {
            var name = request.Name.Trim();
            RequireName(name);
            var source = await SelinuxHost.GenerateAsync(client, name, request.EventIds, cancellationToken);
            return new SelinuxResponse("ready", null, null, source);
        }, cancellationToken);

    [HttpPost("install")]
    [ProducesResponseType<SelinuxResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SelinuxResponse>> Install(SelinuxInstallRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client =>
        {
            var name = SelinuxHost.DeclaredModule(request.Source)
                ?? throw new SessionException(SessionErrorCodes.BadRequest, "The policy must start with its name and version, e.g. “module my_policy 1.0;”.");
            RequireName(name);

            var before = await SelinuxHost.GetStatusAsync(client, DateTimeOffset.UtcNow, null, cancellationToken);
            if (before.Modules.Any(m => m.Name == name && m.Priority == SelinuxHost.DistributionPriority))
            {
                throw new SessionException(SessionErrorCodes.BadRequest, $"The distribution has a module called {name}. Choose another name, so as not to override it.");
            }

            await SelinuxHost.InstallAsync(client, name, request.Source, cancellationToken);
            var after = await SelinuxHost.GetStatusAsync(client, DateTimeOffset.UtcNow, null, cancellationToken);
            if (!after.Modules.Any(m => m.Name == name))
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, $"semodule reported success, but the host doesn't list {name}.");
            }

            await audit.RecordAsync(new AuditEntry("selinux.install", request.HostId, $"Installed policy {name}, as {User.Identity?.Name}"));
            return new SelinuxResponse("ready", null, null, Message: $"Installed {name}.");
        }, cancellationToken);

    [HttpPost("remove")]
    [ProducesResponseType<SelinuxResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<SelinuxResponse>> Remove(SelinuxRemoveRequest request, CancellationToken cancellationToken) =>
        WithHostAsync(request, async client =>
        {
            if (request.Priority == SelinuxHost.DistributionPriority)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, $"{request.Name} is part of the distribution's policy, which the system depends on, so NKeeper doesn't remove it.");
            }

            var before = await SelinuxHost.GetStatusAsync(client, DateTimeOffset.UtcNow, null, cancellationToken);
            var module = before.Modules.FirstOrDefault(m => m.Name == request.Name && m.Priority == request.Priority)
                ?? throw new SessionException(SessionErrorCodes.BadRequest, $"The host has no module {request.Name} at priority {request.Priority}. Refresh and try again.");

            await SelinuxHost.RemoveAsync(client, module, cancellationToken);
            var after = await SelinuxHost.GetStatusAsync(client, DateTimeOffset.UtcNow, null, cancellationToken);
            if (after.Modules.Contains(module))
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, $"semodule reported success, but the host still lists {module.Name}.");
            }

            await audit.RecordAsync(new AuditEntry("selinux.remove", request.HostId, $"Removed policy {module.Name} (priority {module.Priority}), as {User.Identity?.Name}"));
            return new SelinuxResponse("ready", null, null, Message: $"Removed {module.Name}.");
        }, cancellationToken);

    private static void RequireName(string name)
    {
        if (!SelinuxHost.IsModuleName(name))
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "A policy's name is 1 to 64 letters, digits or underscores, starting with a letter.");
        }
    }

    private async Task<ActionResult<SelinuxResponse>> WithHostAsync(SelinuxRequest request, Func<SshClient, Task<SelinuxResponse>> action, CancellationToken cancellationToken)
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
            return new SelinuxResponse("trustRequired", null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }
}
