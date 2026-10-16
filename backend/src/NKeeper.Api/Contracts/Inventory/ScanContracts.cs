using System.ComponentModel.DataAnnotations;
using NKeeper.Api.Controllers;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Contracts.Inventory;

/// <summary>
/// An internal scan: which SSH service and login to use (a saved credential, or a one-off username
/// and password), and optionally an SSH host key the user chose to trust. The host comes from the URL.
/// </summary>
public sealed record InternalScanRequest : SessionStartMessage
{
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }
}

/// <summary>A listening (internal scan) or open (external scan) TCP port, with a well-known name when there is one.</summary>
public sealed record ScannedPort(int Port, string? Name, ServiceProtocol? Protocol);

/// <summary>A local account that can log in (a real shell), from the host's user database.</summary>
public sealed record ScannedUser(string Username, int Uid, string? FullName, string Shell);

/// <summary>
/// What a scan found. Nothing is saved: the client shows the findings and applies the ones the user
/// picks with an ordinary host update.
/// </summary>
/// <param name="IpAddress">The address the host was reached on.</param>
/// <param name="Ports">Every listening or open port found; those with a <see cref="ScannedPort.Protocol"/> can become services.</param>
/// <param name="Users">Internal scans only.</param>
/// <param name="Notes">How values were found, and what couldn't be (e.g. no MAC address across a router).</param>
public sealed record ScanFindings(
    string? Hostname,
    string? IpAddress,
    string? MacAddress,
    string? OperatingSystem,
    IReadOnlyList<ScannedPort> Ports,
    bool IsKvmHost,
    IReadOnlyList<ScannedUser> Users,
    IReadOnlyList<string> Notes);

/// <param name="Status">"ready", or "trustRequired" when the SSH host key must be confirmed first (internal scans).</param>
public sealed record ScanResponse(string Status, ScanFindings? Findings, HostKeyPromptResponse? HostKey);
