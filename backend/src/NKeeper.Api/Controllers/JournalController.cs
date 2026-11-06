using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.Journal;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Controllers;

/// <summary>Which host, SSH service, login and entries to read, newest first.</summary>
public sealed record JournalRequest : SessionStartMessage
{
    /// <summary>The SSH host key fingerprint the user chose to trust after a "trustRequired" answer.</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>This syslog priority and worse: 0 (emergency) to 7 (debug).</summary>
    [Range(0, 7)]
    public int? MaxPriority { get; init; }

    [StringLength(256)]
    public string? Unit { get; init; }

    [StringLength(256)]
    public string? Identifier { get; init; }

    /// <summary>A regular expression on the message.</summary>
    [StringLength(1024)]
    public string? Search { get; init; }

    /// <summary>Only entries older than this one: the last of the previous page.</summary>
    [StringLength(512)]
    public string? After { get; init; }
}

/// <summary>The entries, or the SSH host key to trust first.</summary>
public sealed record JournalResponse(string Status, JournalPage? Page, HostKeyPromptResponse? HostKey);

/// <summary>
/// A Linux host's systemd journal over SSH, read only. Reading is audited; paging back isn't. Following
/// new entries is a WebSocket (see <see cref="JournalFollowSession"/>).
/// </summary>
[ApiController]
[Authorize]
[Route("api/journal")]
public sealed class JournalController(AuditLog audit) : ControllerBase
{
    private const int DefaultSshPort = 22;

    [HttpPost("entries")]
    [ProducesResponseType<JournalResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<JournalResponse>> Entries(JournalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Since > request.Until)
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "The start is after the stop.");
            }

            if (!JournalHost.IsCursor(request.After))
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "That isn't a journal cursor.");
            }

            var services = HttpContext.RequestServices;
            var target = await services.GetRequiredService<SessionTargetResolver>()
                .ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            using var client = await SshCommandConnection.ConnectAsync(
                target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
            var page = await JournalHost.ReadAsync(
                client, new JournalQuery(request.Since, request.Until, request.MaxPriority, request.Unit, request.Identifier, request.Search, request.After), cancellationToken);
            if (request.After is null)
            {
                await audit.RecordAsync(new AuditEntry("journal.read", request.HostId, $"as {target.Username}"));
            }

            return new JournalResponse("ready", page, null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new JournalResponse("trustRequired", null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }
}
