using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace NKeeper.Api.RemoteSessions;

/*
 * Remote session WebSocket protocols (GET /api/ssh/session, GET /api/rdp/session) share a JSON
 * control phase in text frames, each with a "type" field:
 *
 *   client → server  start           { hostId, serviceIndex, credentialId? | useSignInCredentials? | username + password (+ domain)?, ...protocol fields }
 *                    hostKeyDecision { trust }                         (reply to hostKey "unknown")
 *   server → client  status          { status: "connecting", target }
 *                    hostKey         { status: "unknown" | "changed", kind: "ssh" | "certificate", port, algorithm,
 *                                      fingerprint, trustedFingerprint?, subject?, expires? }
 *                    connected       { target }
 *                    error           { code, message }                (then the socket closes)
 *
 * After "connected", SSH carries raw terminal bytes in binary frames (plus "resize" and "closed"
 * JSON messages), and RDP carries Guacamole protocol instructions in text frames.
 * Secrets are only ever sent client → server (for one-off credentials) and never echoed back.
 */

/// <summary>Fields common to every session's first message.</summary>
public abstract record SessionStartMessage
{
    public Guid HostId { get; init; }

    /// <summary>Index into the host's ordered services; it must be of the session's protocol.</summary>
    [Range(0, 100)]
    public int ServiceIndex { get; init; }

    /// <summary>A saved credential of the host. When null, <see cref="Username"/> and <see cref="Password"/> are used.</summary>
    public Guid? CredentialId { get; init; }

    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(1024)]
    public string? Password { get; init; }

    /// <summary>
    /// RDP: log in with the domain account the user signed in to NKeeper with (Active Directory).
    /// The password stays on the server; the browser only asks for it to be used.
    /// </summary>
    public bool UseSignInCredentials { get; init; }
}

/// <summary>Error codes sent in "error" messages, so the client can tailor its message.</summary>
public static class SessionErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string TooManySessions = "too_many_sessions";
    public const string HostNotFound = "host_not_found";
    public const string WrongService = "wrong_service";
    public const string CredentialNotFound = "credential_not_found";

    /// <summary>The sign-in session holds no domain login to pass on (not an Active Directory sign-in, or pass-through is off).</summary>
    public const string SignInCredentialsUnavailable = "sign_in_credentials_unavailable";
    public const string MissingUsername = "missing_username";
    public const string MissingSecret = "missing_secret";
    public const string InvalidPrivateKey = "invalid_private_key";
    public const string HostKeyRejected = "host_key_rejected";
    public const string HostKeyChanged = "host_key_changed";
    public const string AuthenticationFailed = "authentication_failed";
    public const string ConnectionFailed = "connection_failed";
    public const string Timeout = "timeout";

    /// <summary>SFTP: the server refused a file operation (missing, not allowed, not empty…).</summary>
    public const string FileOperationFailed = "file_operation_failed";

    /// <summary>SFTP: an upload would replace a file that exists.</summary>
    public const string FileExists = "file_exists";

    /// <summary>RDP: the server's security does not match the service's setting, or is insecure.</summary>
    public const string SecurityNegotiationFailed = "security_negotiation_failed";

    /// <summary>RDP: the connection gateway (guacd) is not reachable.</summary>
    public const string GatewayUnavailable = "gateway_unavailable";
}

/// <summary>Checks shared by session types.</summary>
internal static class SessionChecks
{
    internal static void RefuseJumpHost(SessionTarget target, string what)
    {
        if (target.Jump is not null)
        {
            throw new SessionException(
                SessionErrorCodes.ConnectionFailed,
                $"{what} can't go through a jump host yet, and “{target.HostName}” is reached through “{target.Jump.HostName}”. Use SSH, Telnet or a web session, or connect from a computer that can reach it.");
        }
    }
}

internal static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>A setup failure reported to the client as an "error" message.</summary>
internal sealed class SessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
