using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Api.Access;
using NKeeper.Infrastructure.Auditing;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Auditing;

/// <summary>The audit trail's settings (section <c>Auditing</c>).</summary>
public sealed class AuditSettings
{
    public const string SectionName = "Auditing";

    /// <summary>Keep a playable recording of what SSH and Telnet sessions showed.</summary>
    public bool RecordTerminalSessions { get; set; } = true;

    /// <summary>A recording stops growing at this size (uncompressed); the session goes on.</summary>
    [Range(1, 1024)]
    public int MaxRecordingMegabytes { get; set; } = 50;

    /// <summary>Audit events older than this are deleted.</summary>
    [Range(1, 36_500)]
    public int RetentionDays { get; set; } = 365;

    /// <summary>Recordings older than this are deleted (they're much larger than events).</summary>
    [Range(1, 36_500)]
    public int RecordingRetentionDays { get; set; } = 90;
}

/// <summary>The names of audited actions, as stored in <see cref="AuditEvent.Action"/>.</summary>
public static class AuditActions
{
    public const string SignIn = "auth.signin";
    public const string SignInFailed = "auth.signin_failed";
    public const string SignOut = "auth.signout";
    public const string SessionStart = "session.start";
    public const string SessionEnd = "session.end";
    public const string SessionFailed = "session.failed";
}

/// <summary>What to record; the user, client address and names are filled in when left out.</summary>
public sealed record AuditEntry(
    string Action,
    Guid? HostId = null,
    string? Detail = null,
    string? SessionId = null,
    Guid? RecordingId = null,
    Guid? UserId = null,
    string? UserName = null,
    string? HostName = null);

/// <summary>
/// Writes the audit trail. Every write uses its own database context, so an audit row is never part
/// of (or rolled back with) the change it describes, and never fails that change: a failed write is
/// logged instead.
/// </summary>
public sealed class AuditLog(
    IServiceScopeFactory scopes,
    IHttpContextAccessor httpContext,
    IOptions<AuditSettings> settings,
    TimeProvider timeProvider,
    ILogger<AuditLog> logger)
{
    public AuditSettings Settings => settings.Value;

    public async Task RecordAsync(AuditEntry entry)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            var context = httpContext.HttpContext;
            var userId = entry.UserId;
            if (userId is null && context?.User.Identity?.IsAuthenticated == true)
            {
                userId = RequestUser.From(context.User).Id;
            }

            var userName = entry.UserName ?? (userId is { } uid
                ? await db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefaultAsync(CancellationToken.None)
                : null);
            var hostName = entry.HostName ?? (entry.HostId is { } hid
                ? await db.Hosts.Where(h => h.Id == hid).Select(h => h.Name).FirstOrDefaultAsync(CancellationToken.None)
                : null);
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAtUtc = timeProvider.GetUtcNow(),
                UserId = userId,
                UserName = userName,
                Action = entry.Action,
                HostId = entry.HostId,
                HostName = hostName,
                Detail = entry.Detail is { Length: > AuditEvent.DetailMaxLength } detail ? detail[..AuditEvent.DetailMaxLength] : entry.Detail,
                SessionId = entry.SessionId,
                ClientAddress = context?.Connection.RemoteIpAddress?.ToString(),
                RecordingId = entry.RecordingId,
            });

            // Not cancellable: the action already happened, and its record must not depend on the request staying open.
            await db.SaveChangesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The audit trail must never break the action it records.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Couldn't record audit event {Action} for host {HostId}", entry.Action, entry.HostId);
        }

    }

    /// <summary>Starts auditing one remote session; nothing is written until it connects or fails.</summary>
    /// <param name="protocol">The protocol's display name, e.g. "SSH".</param>
    /// <param name="record">Whether to record the terminal (SSH and Telnet), subject to the settings.</param>
    public AuditedSession BeginSession(string protocol, Guid hostId, RequestUser user, bool record = false) =>
        new(this, protocol, hostId, user, record && settings.Value.RecordTerminalSessions, timeProvider);

    /// <summary>Saves a finished recording.</summary>
    internal async Task<Guid?> SaveRecordingAsync(SessionRecording recording)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            recording.UserName ??= recording.UserId is { } uid ? await db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefaultAsync() : null;
            recording.HostName ??= recording.HostId is { } hid ? await db.Hosts.Where(h => h.Id == hid).Select(h => h.Name).FirstOrDefaultAsync() : null;
            db.SessionRecordings.Add(recording);
            await db.SaveChangesAsync(CancellationToken.None);
            return recording.Id;
        }
#pragma warning disable CA1031 // A recording that can't be saved mustn't break the session's end.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Couldn't save the recording of session {SessionId}", recording.SessionId);
            return null;
        }
    }

    /// <summary>A new session id: random, so it reveals nothing and can't be guessed.</summary>
    internal static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
}

/// <summary>
/// The audit record of one remote session: "session.start" when it connects (with the target),
/// "session.failed" when it can't, and "session.end" with its duration, plus its terminal recording.
/// </summary>
public sealed class AuditedSession
{
    private readonly AuditLog _log;
    private readonly string _protocol;
    private readonly Guid _hostId;
    private readonly RequestUser _user;
    private readonly TimeProvider _time;
    private readonly bool _record;
    private DateTimeOffset _connectedAt;
    private string? _target;
    private bool _connected;
    private bool _ended;

    internal AuditedSession(AuditLog log, string protocol, Guid hostId, RequestUser user, bool record, TimeProvider time)
    {
        _log = log;
        _protocol = protocol;
        _hostId = hostId;
        _user = user;
        _record = record;
        _time = time;
        Id = AuditLog.NewSessionId();
    }

    public string Id { get; }

    /// <summary>The terminal recorder, once connected (null when not recording).</summary>
    public TerminalRecorder? Recorder { get; private set; }

    /// <param name="target">What it connected to, e.g. "root@10.0.0.5:22".</param>
    public Task ConnectedAsync(string target, int cols = 80, int rows = 24)
    {
        _connected = true;
        _target = target;
        _connectedAt = _time.GetUtcNow();
        if (_record)
        {
            Recorder = new TerminalRecorder(cols, rows, _connectedAt, $"{_protocol} {target}", _log.Settings.MaxRecordingMegabytes * 1024L * 1024L, _time);
        }

        return _log.RecordAsync(new AuditEntry(AuditActions.SessionStart, _hostId, $"{_protocol} · {target}", Id, UserId: _user.Id));
    }

    /// <summary>A failure: before connecting it's "session.failed"; after, the session's end, with the reason.</summary>
    public Task FailedAsync(string reason, string? target = null)
    {
        if (_connected)
        {
            return EndAsync(reason);
        }

        if (_ended)
        {
            return Task.CompletedTask;
        }

        _ended = true;
        return _log.RecordAsync(new AuditEntry(AuditActions.SessionFailed, _hostId, $"{_protocol} · {target ?? _target ?? "?"} · {reason}", Id, UserId: _user.Id));
    }

    /// <summary>Records the end (if it connected) and saves the recording. Safe to call more than once.</summary>
    public async Task EndAsync(string? reason = null)
    {
        if (_ended || !_connected)
        {
            return;
        }

        _ended = true;
        var ended = _time.GetUtcNow();
        Guid? recordingId = null;
        if (Recorder is { } recorder)
        {
            using (recorder)
            {
                recordingId = await _log.SaveRecordingAsync(recorder.Finish(Id, _user.Id, _hostId, _protocol, ended));
            }
        }

        var duration = ended - _connectedAt;
        await _log.RecordAsync(new AuditEntry(
            AuditActions.SessionEnd,
            _hostId,
            $"{_protocol} · {_target} · after {FormatDuration(duration)}{(reason is null ? "" : $" · {reason}")}",
            Id,
            recordingId,
            UserId: _user.Id));
    }

    internal static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
        : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds}s"
        : $"{Math.Max(0, duration.Seconds)}s";
}
