using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Net.WebSockets;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;

namespace NKeeper.Api.Journal;

/// <summary>The first message of a follow: which host and login, the filters, and the newest entry already shown.</summary>
public sealed record JournalFollowStartMessage : SessionStartMessage
{
    [Range(0, 7)]
    public int? MaxPriority { get; init; }

    [StringLength(256)]
    public string? Unit { get; init; }

    [StringLength(256)]
    public string? Identifier { get; init; }

    [StringLength(1024)]
    public string? Search { get; init; }

    [StringLength(512)]
    public string? After { get; init; }
}

/// <summary>
/// Streams new journal entries for as long as the browser wants them, as <c>{ type: "entry", entry }</c>.
/// It keeps one SSH connection, because logging in to poll would itself write to the journal being
/// watched. The host key must already be trusted (the tab read the journal first).
/// </summary>
internal sealed class JournalFollowSession(SessionTargetResolver resolver, TrustedKeyStore trustedKeys, AuditLog audit, ILogger<JournalFollowSession> logger)
{
    private const int DefaultSshPort = 22;

    public async Task RunAsync(SessionSocket socket, RequestUser user, CancellationToken cancellationToken)
    {
        try
        {
            var start = await socket.ReceiveStartAsync<JournalFollowStartMessage>(cancellationToken);
            if (!JournalHost.IsCursor(start.After))
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "That isn't a journal cursor.");
            }

            var target = await resolver.ResolveAsync(start, user, ServiceProtocol.Ssh, DefaultSshPort, cancellationToken);
            using var client = await SshCommandConnection.ConnectAsync(target, null, user.Id, trustedKeys, cancellationToken);
            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);
            await audit.RecordAsync(new AuditEntry("journal.follow", start.HostId, $"as {target.Username}"));

            var query = new JournalQuery(null, null, start.MaxPriority, start.Unit, start.Identifier, start.Search, start.After);
            using var command = client.CreateCommand(JournalHost.FollowScript(query));
            await PumpAsync(socket, client, command, cancellationToken);
        }
        catch (HostKeyTrustRequiredException)
        {
            await socket.SendErrorAndCloseAsync(SessionErrorCodes.HostKeyRejected, "Refresh the journal to confirm this host's key first.", cancellationToken);
        }
        catch (SessionException ex)
        {
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away, or stopped following.
        }
        catch (Exception ex) when (ex is IOException or SocketException or Renci.SshNet.Common.SshException)
        {
            logger.LogInformation(ex, "Following a journal for user {UserId} ended", user.Id);
            await socket.SendErrorAndCloseAsync(SessionErrorCodes.ConnectionFailed, "The connection to the host was lost.", cancellationToken);
        }
    }

    private static async Task PumpAsync(SessionSocket socket, SshClient client, SshCommand command, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var execution = command.ExecuteAsync(stop.Token);
        var entries = SendEntriesAsync(socket, command, stop.Token);
        var browser = WaitForCloseAsync(socket, stop.Token);

        if (await Task.WhenAny(execution, browser) == browser)
        {
            // Stopped: dropping the connection ends journalctl, even under sudo.
            await stop.CancelAsync();
            client.Disconnect();
            await socket.AcknowledgeCloseAsync();
            await Task.WhenAll(execution, entries).ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }

        await execution;
        await entries.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (command.ExitStatus is not 0 && command.Error.Trim() is { Length: > 0 } error)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, JournalHost.Explain(error));
        }

        await socket.SendJsonAsync(new { type = "closed", message = "journalctl stopped." }, cancellationToken);
        await socket.BeginCloseAsync("Ended", cancellationToken);
        await stop.CancelAsync();
        await browser.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static async Task SendEntriesAsync(SessionSocket socket, SshCommand command, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(command.OutputStream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (JournalHost.ParseEntry(line) is { } entry)
            {
                await socket.SendJsonAsync(new { type = "entry", entry }, cancellationToken);
            }
        }
    }

    private static async Task WaitForCloseAsync(SessionSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            var (type, _) = await socket.ReceiveAsync(cancellationToken);
            if (type == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }
}
