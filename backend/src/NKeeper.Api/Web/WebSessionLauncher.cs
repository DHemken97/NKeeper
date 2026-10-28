using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Controllers;
using NKeeper.Sdk.Auditing;
using NKeeper.Sdk.Security;
using NKeeper.Sdk.Sessions;
using NKeeper.Sdk.Web;

namespace NKeeper.Api.Web;

/// <summary>Opens web sessions for plugins, as <c>POST /api/web/sessions</c> does for a host's web services.</summary>
internal sealed class WebSessionLauncher(WebSessionStore sessions, IOptions<WebProxySettings> settings, AuditLog audit) : IWebSessions
{
    public async Task<OpenWebSessionResponse> OpenAsync(
        HttpContext context, Guid hostId, Uri target, string? pinnedFingerprint, string startPath, JumpTunnel? tunnel, string auditDetail)
    {
        // The session owns the tunnel from here on.
        var session = sessions.TryCreate(context.User.GetUserId(), hostId, target, pinnedFingerprint, WebSessionsController.EmbedderOrigin(context.Request), startPath, tunnel)
            ?? throw new SessionException(
                SessionErrorCodes.TooManySessions, $"You already have {WebSessionStore.MaxSessionsPerUser} open web sessions. Close one and try again.");
        await audit.RecordAsync(new AuditEntry(AuditActions.SessionStart, hostId, auditDetail, session.Id));
        return new OpenWebSessionResponse(
            "ready", session.Id, $"{WebProxyMiddleware.LaunchPath}?t={session.LaunchToken}", settings.Value.BaseDomain, target.ToString(), null);
    }
}
