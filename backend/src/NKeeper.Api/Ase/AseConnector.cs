using System.Data.Common;
using System.Text;
using AdoNetCore.AseClient;
using NKeeper.Api.Access;
using NKeeper.Api.Databases;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Ase;

/// <summary>
/// Opens SAP (Sybase) ASE sessions from the inventory: the host's ASE service and a login. The
/// password is sent encrypted when the server supports it; the rest of the connection is as the
/// server is set up (ASE's own SSL isn't used). With <paramref name="replicationServer"/>, opens a Replication Server's
/// service instead: it speaks the same protocol, but only its own admin commands.
/// </summary>
internal sealed class AseConnector(SessionTargetResolver resolver, TimeProvider timeProvider, bool replicationServer = false) : IDatabaseConnector
{
    public const int DefaultPort = 5000;
    public const int ReplicationServerPort = 11752;
    private const int ConnectTimeoutSeconds = 20;

    static AseConnector()
    {
        // ASE servers often use single-byte character sets such as iso_1 and cp850.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public DatabaseEngine Engine => replicationServer ? DatabaseEngine.RepServer : DatabaseEngine.Ase;

    public async Task<DatabaseOpenResult> OpenAsync(DatabaseOpenRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var defaultPort = replicationServer ? ReplicationServerPort : DefaultPort;
        var target = await resolver.ResolveAsync(request, user, replicationServer ? ServiceProtocol.RepServer : ServiceProtocol.Ase, defaultPort, cancellationToken);
        if (target.Domain is not null)
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "These logins have no domain: use a credential without one.");
        }

        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        var port = target.Service.Port ?? defaultPort;
        var display = $"{target.Username}@{target.Address}:{port}";
        var tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, port, cancellationToken);
        AseConnection? connection = null;
        try
        {
            var settings = new DbConnectionStringBuilder
            {
                ["Data Source"] = tunnel.Address,
                ["Port"] = tunnel.Port,
                ["Uid"] = target.Username,
                ["Pwd"] = target.Password,
                ["ApplicationName"] = "NKeeper",
                ["LoginTimeOut"] = ConnectTimeoutSeconds,
                ["EncryptPassword"] = 1,
                // Each session holds its own connections; a pool would keep logins open after it ends.
                ["Pooling"] = false,
                ["TextSize"] = 65536,
                // The driver won't connect without a database; master is in every server and readable by every login.
                ["Database"] = string.IsNullOrWhiteSpace(request.Database) || replicationServer ? "master" : request.Database.Trim(),
            };

            var connectionString = settings.ConnectionString;
            try
            {
                connection = await OpenAsync(connectionString, cancellationToken);
            }
            catch (AseException ex)
            {
                throw SignInFailure(ex, target.Username, display);
            }
            catch (ArgumentException ex)
            {
                // The driver checks its settings before connecting (e.g. a character set it doesn't know).
                throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {display}: {ex.Message}");
            }

            var server = replicationServer
                ? await ReadReplicationServerInfoAsync(connection, target.Username, display, cancellationToken)
                : await ReadServerInfoAsync(connection, cancellationToken);
            var keptTunnel = tunnel.IsTunnel ? tunnel : null;
            var session = new DatabaseSession(
                Engine,
                user.Id,
                target.HostId,
                display,
                connection,
                server,
                async ct => await OpenAsync(connectionString, ct),
                () => keptTunnel?.Dispose(),
                timeProvider.GetUtcNow());
            if (!tunnel.IsTunnel)
            {
                tunnel.Dispose();
            }

            connection = null;
            return new DatabaseOpenResult(session, null, display);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            tunnel.Dispose();
            throw;
        }
    }

    private static async Task<AseConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new AseConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<DatabaseServerInfo> ReadServerInfoAsync(AseConnection connection, CancellationToken cancellationToken)
    {
        var row = (await DbQuery.ListAsync(
            connection,
            "SELECT @@servername, @@version, suser_name(), @@spid",
            [],
            r => (Name: DbQuery.Text(r, 0), Version: DbQuery.Text(r, 1), Login: DbQuery.Text(r, 2), Spid: DbQuery.Int(r, 3)),
            cancellationToken))[0];

        // e.g. "Adaptive Server Enterprise/16.0 SP03 PL08/EBF 29704 SMP/P/x86_64/SLES 12.4/…": 16.0 SP03 PL08.
        var parts = row.Version?.Split('/') ?? [];
        var version = parts.Length > 1 ? parts[1] : row.Version ?? string.Empty;
        return new DatabaseServerInfo(row.Name ?? connection.DataSource, version, null, parts.Length > 0 ? parts[0] : null, row.Login ?? string.Empty, row.Spid);
    }

    /// <summary>Replication Server has no @@version: "admin version" says e.g. "Replication Server/16.0 SP04 PL01/…".</summary>
    private static async Task<DatabaseServerInfo> ReadReplicationServerInfoAsync(AseConnection connection, string username, string display, CancellationToken cancellationToken)
    {
        string? text = null;
        try
        {
            var rows = await DbQuery.ListAsync(connection, "admin version", [], r => DbQuery.Text(r, 0), cancellationToken);
            text = rows.Count > 0 ? rows[0] : null;
        }
        catch (AseException)
        {
            // Older servers answer with a message only; the version is then unknown.
        }

        var parts = text?.Split('/') ?? [];
        return new DatabaseServerInfo(display, parts.Length > 1 ? parts[1] : text ?? string.Empty, null, parts.Length > 0 ? parts[0] : null, username, 0);
    }

    private static SessionException SignInFailure(AseException ex, string username, string display)
    {
        var number = ex.Errors.Count > 0 ? ex.Errors[0].MessageNumber : 0;
        return number switch
        {
            4002 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"ASE refused the login “{username}”. Check the username and password."),
            4022 or 4023 => new SessionException(SessionErrorCodes.AuthenticationFailed, ex.Message),
            _ => new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't sign in to {display}: {ex.Message}"),
        };
    }
}
