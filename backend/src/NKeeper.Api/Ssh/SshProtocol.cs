using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace NKeeper.Api.Ssh;

/*
 * Terminal WebSocket protocol (GET /api/ssh/session):
 *
 * Binary frames carry raw terminal bytes in both directions. Text frames carry JSON control
 * messages with a "type" field:
 *
 *   client → server  start         { hostId, serviceIndex, credentialId? | username + password?, cols, rows }  (first message)
 *                    resize        { cols, rows }
 *                    hostKeyDecision { trust }                         (reply to hostKey "unknown")
 *   server → client  status        { status: "connecting", target }
 *                    hostKey       { status: "unknown" | "changed", port, algorithm, fingerprint, trustedFingerprint? }
 *                    connected     { target }
 *                    error         { code, message }                  (then the socket closes)
 *                    closed        { message }                        (the remote shell ended)
 *
 * Secrets are only ever sent client → server (for one-off credentials) and never echoed back.
 */

/// <summary>The first message of a terminal session.</summary>
public sealed record SshStartMessage
{
    public Guid HostId { get; init; }

    /// <summary>Index into the host's ordered services; that service must be SSH.</summary>
    [Range(0, 100)]
    public int ServiceIndex { get; init; }

    /// <summary>A saved credential of the host. When null, <see cref="Username"/> and <see cref="Password"/> are used.</summary>
    public Guid? CredentialId { get; init; }

    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(1024)]
    public string? Password { get; init; }

    [Range(1, 1000)]
    public int Cols { get; init; } = 80;

    [Range(1, 1000)]
    public int Rows { get; init; } = 24;
}

/// <summary>Error codes sent in "error" messages, so the client can tailor its message.</summary>
public static class SshErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string TooManySessions = "too_many_sessions";
    public const string HostNotFound = "host_not_found";
    public const string NotSshService = "not_ssh_service";
    public const string CredentialNotFound = "credential_not_found";
    public const string MissingUsername = "missing_username";
    public const string MissingSecret = "missing_secret";
    public const string InvalidPrivateKey = "invalid_private_key";
    public const string HostKeyRejected = "host_key_rejected";
    public const string HostKeyChanged = "host_key_changed";
    public const string AuthenticationFailed = "authentication_failed";
    public const string ConnectionFailed = "connection_failed";
    public const string Timeout = "timeout";
}

internal static class SshJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>A setup failure reported to the client as an "error" message.</summary>
internal sealed class SshSessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
