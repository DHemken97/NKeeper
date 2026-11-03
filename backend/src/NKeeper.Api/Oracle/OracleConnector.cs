using System.Data.Common;
using NKeeper.Api.Access;
using NKeeper.Api.Databases;
using NKeeper.Api.RemoteSessions;
using NKeeper.Infrastructure.Inventory;
using Oracle.ManagedDataAccess.Client;

namespace NKeeper.Api.Oracle;

/// <summary>
/// Opens Oracle sessions (11g and later) from the inventory: the host's Oracle service (its service
/// name, or SID) and a database login. Oracle's native network encryption and integrity checks are
/// requested, so a server that offers them encrypts the connection. A login named SYS connects AS SYSDBA.
/// </summary>
internal sealed class OracleConnector(SessionTargetResolver resolver, TimeProvider timeProvider) : IDatabaseConnector
{
    public const int DefaultPort = 1521;
    private const int ConnectTimeoutSeconds = 20;

    static OracleConnector()
    {
        OracleConfiguration.SqlNetEncryptionClient = "requested";
        OracleConfiguration.SqlNetCryptoChecksumClient = "requested";
    }

    public DatabaseEngine Engine => DatabaseEngine.Oracle;

    public async Task<DatabaseOpenResult> OpenAsync(DatabaseOpenRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var target = await resolver.ResolveAsync(request, user, ServiceProtocol.Oracle, DefaultPort, cancellationToken);
        if (target.Domain is not null)
        {
            throw new SessionException(SessionErrorCodes.BadRequest, "Oracle logins have no domain: use a credential without one.");
        }

        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        var service = target.Service.Instance
            ?? throw new SessionException(SessionErrorCodes.BadRequest, "This Oracle service has no service name. Edit the host to add it (e.g. ORCLPDB1).");
        var port = target.Service.Port ?? DefaultPort;
        var display = $"{target.Username}@{target.Address}:{port}/{service}";
        var tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, port, cancellationToken);
        OracleConnection? connection = null;
        try
        {
            OracleConnectionStringBuilder Settings(string connectData) => new()
            {
                DataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={tunnel.Address})(PORT={tunnel.Port}))(CONNECT_DATA=({connectData}={service})))",
                UserID = target.Username,
                Password = target.Password,
                // Each session holds its own connections; a pool would keep logins open after it ends.
                Pooling = false,
                ConnectionTimeout = ConnectTimeoutSeconds,
                DBAPrivilege = string.Equals(target.Username, "SYS", StringComparison.OrdinalIgnoreCase) ? "SYSDBA" : string.Empty,
            };

            var settings = Settings("SERVICE_NAME");
            try
            {
                connection = await OpenAsync(settings, cancellationToken);
            }
            catch (OracleException ex) when (ex.Number == 12514)
            {
                // Not a service the listener knows: it may be a SID (older databases are often reached by one).
                settings = Settings("SID");
                try
                {
                    connection = await OpenAsync(settings, cancellationToken);
                }
                catch (OracleException)
                {
                    throw SignInFailure(ex, target.Username, display);
                }
            }
            catch (OracleException ex)
            {
                throw SignInFailure(ex, target.Username, display);
            }

            if (!string.IsNullOrWhiteSpace(request.Database))
            {
                await OracleDialect.SetSchemaAsync(connection, request.Database.Trim(), cancellationToken);
            }

            // DBMS_OUTPUT (PUT_LINE) is shown under Messages, as SQL Developer does with it on.
            await DbQuery.ExecuteAsync(connection, "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;", cancellationToken);
            var server = await ReadServerInfoAsync(connection, cancellationToken);
            var keptTunnel = tunnel.IsTunnel ? tunnel : null;
            var connectionString = settings.ConnectionString;
            var session = new DatabaseSession(
                DatabaseEngine.Oracle,
                user.Id,
                target.HostId,
                display,
                connection,
                server,
                async ct => await OpenAsync(new OracleConnectionStringBuilder(connectionString), ct),
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

    private static async Task<OracleConnection> OpenAsync(OracleConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(settings.ConnectionString);
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

    private static async Task<DatabaseServerInfo> ReadServerInfoAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        var row = (await DbQuery.ListAsync(
            connection,
            "SELECT SYS_CONTEXT('USERENV','INSTANCE_NAME'), SYS_CONTEXT('USERENV','DB_NAME'), SYS_CONTEXT('USERENV','SESSION_USER'), SYS_CONTEXT('USERENV','SID') FROM dual",
            [],
            r => (Instance: DbQuery.Text(r, 0), Database: DbQuery.Text(r, 1), Login: DbQuery.Text(r, 2), Sid: DbQuery.Int(r, 3)),
            cancellationToken))[0];

        string? edition = null;
        try
        {
            // V$VERSION needs SELECT_CATALOG_ROLE (or similar); without it, only the version number is shown.
            var banner = (await DbQuery.ListAsync(connection, "SELECT banner FROM v$version WHERE ROWNUM = 1", [], r => DbQuery.Text(r, 0), cancellationToken)).FirstOrDefault();
            edition = banner?.Split(" Release ")[0].Replace("Oracle Database ", string.Empty, StringComparison.Ordinal).Trim();
        }
        catch (DbException)
        {
        }

        var name = row.Instance is null ? connection.DataSource : row.Database is null || string.Equals(row.Instance, row.Database, StringComparison.OrdinalIgnoreCase)
            ? row.Instance
            : $"{row.Instance} ({row.Database})";
        return new DatabaseServerInfo(name, connection.ServerVersion, null, edition, row.Login ?? string.Empty, row.Sid);
    }

    private static SessionException SignInFailure(OracleException ex, string username, string display) => ex.Number switch
    {
        1017 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"Oracle refused the login “{username}”. Check the username and password."),
        28000 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"The account “{username}” is locked."),
        28001 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"The password of “{username}” has expired. Change it in another client, then connect again."),
        1045 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"“{username}” lacks CREATE SESSION, so it can't sign in."),
        12514 or 12505 => new SessionException(SessionErrorCodes.ConnectionFailed, $"The listener at {display} doesn't know this service name or SID. Check the service's name on the host."),
        _ => new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't sign in to {display}: {ex.Message}"),
    };
}
