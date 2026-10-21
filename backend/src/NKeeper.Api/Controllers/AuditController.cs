using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Infrastructure.Auditing;
using NKeeper.Infrastructure.Identity;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Controllers;

public sealed record AuditEventResponse(
    long Id,
    DateTimeOffset OccurredAtUtc,
    Guid? UserId,
    string? UserName,
    string Action,
    Guid? HostId,
    string? HostName,
    string? Detail,
    string? SessionId,
    string? ClientAddress,
    Guid? RecordingId);

/// <param name="Next">Pass as <c>before</c> to get the next (older) page; null on the last page.</param>
public sealed record AuditPageResponse(IReadOnlyList<AuditEventResponse> Events, long? Next);

public sealed record RecordingResponse(
    Guid Id, string Protocol, string? UserName, string? HostName, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, long Size, bool Truncated);

/// <summary>Which events to return. All filters combine.</summary>
public sealed record AuditQuery
{
    /// <summary>Text in the user, host, action or detail.</summary>
    [StringLength(200)]
    public string? Q { get; init; }

    /// <summary>"signin", "session", "host", "user", "transfer", "idrac", "storage" or "files".</summary>
    [StringLength(32)]
    public string? Category { get; init; }

    public Guid? UserId { get; init; }

    public Guid? HostId { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>Only events older than this id (paging).</summary>
    public long? Before { get; init; }

    [Range(1, 500)]
    public int Limit { get; init; } = 100;
}

/// <summary>The audit trail and terminal recordings. Administrators only.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Roles = SystemRoles.Administrator)]
public sealed class AuditController(NKeeperDbContext db) : ControllerBase
{
    private const int MaxCsvRows = 100_000;

    [HttpGet("events")]
    [ProducesResponseType<AuditPageResponse>(StatusCodes.Status200OK)]
    public async Task<AuditPageResponse> Events([FromQuery] AuditQuery query, CancellationToken cancellationToken)
    {
        var events = await Filter(query).OrderByDescending(e => e.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var page = events.Take(query.Limit).Select(ToResponse).ToList();
        return new AuditPageResponse(page, events.Count > query.Limit ? page[^1].Id : null);
    }

    /// <summary>The events matching the filters as CSV (newest first, at most 100,000).</summary>
    [HttpGet("events.csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> Csv([FromQuery] AuditQuery query, CancellationToken cancellationToken)
    {
        var csv = new StringBuilder("Time (UTC),User,Action,Host,Detail,Client address,Session\r\n");
        await foreach (var e in Filter(query).OrderByDescending(e => e.Id).Take(MaxCsvRows).AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            csv.AppendJoin(',', [
                Quote(e.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Quote(e.UserName), Quote(e.Action), Quote(e.HostName), Quote(e.Detail), Quote(e.ClientAddress), Quote(e.SessionId)]);
            csv.Append("\r\n");
        }

        Response.Headers.CacheControl = "no-store";
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"nkeeper-audit-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    [HttpGet("recordings/{id:guid}")]
    [ProducesResponseType<RecordingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecordingResponse>> Recording(Guid id, CancellationToken cancellationToken) =>
        await db.SessionRecordings.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new RecordingResponse(r.Id, r.Protocol, r.UserName, r.HostName, r.StartedAtUtc, r.EndedAtUtc, r.Size, r.Truncated))
            .FirstOrDefaultAsync(cancellationToken) is { } recording
            ? recording
            : NotFound();

    /// <summary>The recording itself: an asciicast v2 file.</summary>
    [HttpGet("recordings/{id:guid}/cast")]
    [Produces("application/x-asciicast")]
    public async Task<IActionResult> Cast(Guid id, CancellationToken cancellationToken)
    {
        var data = await db.SessionRecordings.AsNoTracking().Where(r => r.Id == id).Select(r => r.Data).FirstOrDefaultAsync(cancellationToken);
        if (data is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return File(new GZipStream(new MemoryStream(data), CompressionMode.Decompress), "application/x-asciicast", $"session-{id}.cast");
    }

    private IQueryable<AuditEvent> Filter(AuditQuery query)
    {
        var events = db.AuditEvents.AsNoTracking();
        if (query.Before is { } before)
        {
            events = events.Where(e => e.Id < before);
        }

        if (query.UserId is { } userId)
        {
            events = events.Where(e => e.UserId == userId);
        }

        if (query.HostId is { } hostId)
        {
            events = events.Where(e => e.HostId == hostId);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc < to);
        }

        var prefix = query.Category switch
        {
            "signin" => "auth.",
            "session" => "session.",
            "host" => "host.",
            "user" => "user.",
            "transfer" => "transfer.",
            "idrac" => "idrac.",
            "storage" => "storage.",
            "files" => "sftp.",
            _ => null,
        };
        if (prefix is not null)
        {
            events = events.Where(e => e.Action.StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            events = events.Where(e =>
                EF.Functions.ILike(e.Action, pattern) || EF.Functions.ILike(e.UserName ?? "", pattern)
                || EF.Functions.ILike(e.HostName ?? "", pattern) || EF.Functions.ILike(e.Detail ?? "", pattern));
        }

        return events;
    }

    private static AuditEventResponse ToResponse(AuditEvent e) =>
        new(e.Id, e.OccurredAtUtc, e.UserId, e.UserName, e.Action, e.HostId, e.HostName, e.Detail, e.SessionId, e.ClientAddress, e.RecordingId);

    /// <summary>A CSV field, quoted when needed; a leading formula character is neutralised so spreadsheets don't run it.</summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
