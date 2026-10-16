using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using NKeeper.Api.Access;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Rpc;

/// <summary>The first message of an RPC session.</summary>
public sealed record RpcStartMessage : SessionStartMessage
{
    [StringLength(256)]
    public string? Domain { get; init; }

    /// <summary>The interface to bind; defaults to the endpoint mapper (e1af8308-5d1f-11c9-91a4-08002b14a0fa v3.0).</summary>
    [StringLength(36)]
    public string? InterfaceId { get; init; }

    public ushort Major { get; init; } = 3;

    public ushort Minor { get; init; }
}

/// <summary>
/// Bridges one browser WebSocket to one authenticated Windows RPC connection. After "connected" the
/// browser sends <c>{ type: "call", opnum, stub }</c> (stub as hex) and receives
/// <c>{ type: "response", stub }</c> or <c>{ type: "fault", message, status? }</c>.
/// </summary>
internal sealed class RpcSession(SessionTargetResolver resolver, ILogger<RpcSession> logger)
{
    private const int MaxStubBytes = 256 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly Guid EndpointMapper = new("e1af8308-5d1f-11c9-91a4-08002b14a0fa");

    public async Task RunAsync(SessionSocket socket, RequestUser user, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        SessionTarget? target = null;

        try
        {
            var start = await socket.ReceiveStartAsync<RpcStartMessage>(cancellationToken);
            var interfaceId = EndpointMapper;
            if (start.InterfaceId is { Length: > 0 } text && !Guid.TryParse(text, out interfaceId))
            {
                throw new SessionException(SessionErrorCodes.BadRequest, "The interface id is not a valid UUID.");
            }

            target = await resolver.ResolveAsync(start, user, ServiceProtocol.Rpc, RpcClient.EndpointMapperPort, cancellationToken, start.Domain);
            if (target.Password is null)
            {
                throw new SessionException(SessionErrorCodes.MissingSecret, "RPC needs a password.");
            }

            logger.LogInformation(
                "RPC session requested by user {UserId} for host {HostId} ({Address}:{Port}) using {CredentialSource}",
                user.Id, target.HostId, target.Address, target.Port, target.CredentialSource);

            await socket.SendJsonAsync(new { type = "status", status = "connecting", target = target.Display }, cancellationToken);
            await using var client = new RpcClient();
            await ConnectAsync(client, target, interfaceId, start, cancellationToken);
            await socket.SendJsonAsync(new { type = "connected", target = target.Display }, cancellationToken);

            await ServeAsync(socket, client, cancellationToken);
        }
        catch (SessionException ex)
        {
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or the request was aborted.
        }
        finally
        {
            logger.LogInformation("RPC session ended for user {UserId} (host {HostId}) after {Duration}", user.Id, target?.HostId, started.Elapsed);
        }
    }

    private static async Task ConnectAsync(RpcClient client, SessionTarget target, Guid interfaceId, RpcStartMessage start, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(target.Address, target.Port, timeout.Token);
            var credentials = new RpcCredentials(target.Username, target.Password!, target.Domain);
            await client.BindAsync(interfaceId, start.Major, start.Minor, credentials, target.Address, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"{target.Address}:{target.Port} didn't answer in time.");
        }
        catch (RpcException ex)
        {
            throw new SessionException(SessionErrorCodes.AuthenticationFailed, ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {target.Address}:{target.Port}.");
        }
    }

    private static async Task ServeAsync(SessionSocket socket, RpcClient client, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            var (type, data) = await socket.ReceiveAsync(cancellationToken);
            if (type == WebSocketMessageType.Close)
            {
                await socket.AcknowledgeCloseAsync();
                return;
            }

            if (type != WebSocketMessageType.Text || SessionSocket.ReadType(data) != "call")
            {
                continue;
            }

            CallMessage? call;
            try
            {
                call = JsonSerializer.Deserialize<CallMessage>(data, SessionJson.Options);
            }
            catch (JsonException)
            {
                call = null;
            }

            byte[]? stub = null;
            if (call is null || call.Stub is not { Length: <= MaxStubBytes * 2 } hex || !TryParseHex(hex, out stub))
            {
                await socket.SendJsonAsync(new { type = "fault", message = "The call needs an opnum and a hex stub." }, cancellationToken);
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallTimeout);
            try
            {
                var response = await client.CallAsync(call.Opnum, stub!, timeout.Token);
                await socket.SendJsonAsync(new { type = "response", stub = Convert.ToHexString(response) }, cancellationToken);
            }
            catch (RpcException ex)
            {
                await socket.SendJsonAsync(new { type = "fault", message = ex.Message, status = ex.Status }, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SessionException(SessionErrorCodes.Timeout, "The server didn't answer the call in time.");
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                throw new SessionException(SessionErrorCodes.ConnectionFailed, "The connection to the server was lost.");
            }
        }
    }

    private static bool TryParseHex(string hex, out byte[]? bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }

    private sealed record CallMessage(ushort Opnum, string? Stub);
}
