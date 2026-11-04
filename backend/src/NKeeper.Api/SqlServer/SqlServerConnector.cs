using Microsoft.Data.SqlClient;
using NKeeper.Api.Access;
using NKeeper.Api.Databases;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// Opens SQL Server sessions from the inventory: the host's SQL Server service (default or named
/// instance) and a SQL login. The connection is always encrypted, and the server's certificate is
/// trusted like an HTTPS server's: a publicly valid one is accepted, any other is confirmed once by the
/// user (trust on first use), and the connection is then pinned to exactly that certificate.
/// </summary>
/// <remarks>
/// A SQL Server without a certificate of its own makes a new self-signed one ("SSL_Self_Signed_Fallback")
/// every time it starts. When the trusted certificate and the new one are both of those, the user is
/// asked again (and the new one replaces the old) instead of the connection being refused as for
/// any other changed certificate. Give the server a real certificate to have changes refused outright.
/// </remarks>
internal sealed class SqlServerConnector(SessionTargetResolver resolver, TrustedKeyStore trustedKeys, TimeProvider timeProvider) : IDatabaseConnector
{
    public DatabaseEngine Engine => DatabaseEngine.SqlServer;

    public const int DefaultPort = 1433;

    /// <summary>The subject of the certificate SQL Server makes for itself at startup when it has none.</summary>
    public const string FallbackSubject = "CN=SSL_Self_Signed_Fallback";
    private const int ConnectTimeoutSeconds = 20;

    /// <exception cref="SessionException">The server can't be reached or refused the login.</exception>
    /// <exception cref="CertificateChangedException">The server's certificate changed since it was trusted.</exception>
    public async Task<DatabaseOpenResult> OpenAsync(DatabaseOpenRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var target = await resolver.ResolveAsync(request, user, ServiceProtocol.Ssms, DefaultPort, cancellationToken);
        if (target.Domain is not null)
        {
            throw new SessionException(
                SessionErrorCodes.BadRequest,
                "NKeeper signs in to SQL Server with SQL logins. Windows (domain) logins aren't supported: use a credential without a domain.");
        }

        if (target.Password is null)
        {
            throw new SessionException(SessionErrorCodes.MissingSecret, "This credential has no password.");
        }

        var instance = target.Service.Instance;
        var port = target.Service.Port ?? DefaultPort;
        if (instance is not null && target.Service.Port is null)
        {
            if (target.Jump is not null)
            {
                throw new SessionException(
                    SessionErrorCodes.BadRequest,
                    $"The instance “{instance}” is reached through a jump host, where the SQL Server Browser can't be asked for its port. Set the instance's port on the service.");
            }

            port = await SqlServerBrowser.ResolvePortAsync(target.Address, instance, cancellationToken);
        }

        var display = $"{target.Username}@{target.Address}{(instance is null ? "" : $"\\{instance}")}:{port}";
        var tunnel = await JumpTunnel.OpenAsync(target.Jump, target.Address, port, cancellationToken);
        string? certificateFile = null;
        SqlConnection? connection = null;
        try
        {
            var certificate = await SqlServerProbe.ProbeAsync(target.Address, port, cancellationToken, tunnel.Address, tunnel.Port);

            // Trust is kept under the service's own port: a named instance's port can change at restart.
            var known = target.KnownKeys.FirstOrDefault(k => k.Algorithm == CertificateTrust.Algorithm);
            var fingerprint = certificate.Certificate.Fingerprint;
            CertificateTrustResult trust;
            if (!certificate.Certificate.IsValid
                && certificate.Certificate.Subject == FallbackSubject
                && known is not null
                && !string.Equals(known.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(request.TrustFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    tunnel.Dispose();
                    return new DatabaseOpenResult(null, certificate.Certificate, display, Renewed: true);
                }

                await trustedKeys.ReplaceAsync(target.HostId, target.Port, target.KeyProtocol, CertificateTrust.Algorithm, fingerprint, user.Id);
                trust = new CertificateTrustResult(fingerprint, null);
            }
            else
            {
                trust = await CertificateTrust.DecideAsync(
                    certificate.Certificate, target.HostId, target.Address, target.Port, target.KeyProtocol, target.KnownKeys, request.TrustFingerprint, user.Id, trustedKeys);
            }

            if (trust.Prompt is { } prompt)
            {
                tunnel.Dispose();
                return new DatabaseOpenResult(null, prompt, display);
            }

            var settings = new SqlConnectionStringBuilder
            {
                DataSource = $"tcp:{tunnel.Address},{tunnel.Port}",
                UserID = target.Username,
                Password = target.Password,
                InitialCatalog = request.Database?.Trim() ?? string.Empty,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = false,
                ApplicationName = "NKeeper",
                ConnectTimeout = ConnectTimeoutSeconds,
                // Each session holds its own connections; a pool would keep logins open after it ends.
                Pooling = false,
                ConnectRetryCount = 0,
            };

            if (trust.PinnedFingerprint is not null)
            {
                // Exactly the certificate the user trusted, for the name it was issued to.
                certificateFile = Path.Combine(Path.GetTempPath(), $"nkeeper-mssql-{Guid.NewGuid():N}.cer");
                await File.WriteAllBytesAsync(certificateFile, certificate.Raw, cancellationToken);
                settings.ServerCertificate = certificateFile;
                settings.HostNameInCertificate = certificate.DnsName;
            }
            else if (tunnel.IsTunnel)
            {
                // A publicly valid certificate is checked for the host's name, not the tunnel's.
                settings.HostNameInCertificate = target.Address;
            }

            connection = new SqlConnection(settings.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                throw SignInFailure(ex, target.Username, display);
            }

            var server = await ReadServerInfoAsync(connection, cancellationToken);
            var keptTunnel = tunnel.IsTunnel ? tunnel : null;
            var keptCertificate = certificateFile;
            var session = new DatabaseSession(
                DatabaseEngine.SqlServer,
                user.Id,
                target.HostId,
                display,
                connection,
                server,
                async ct =>
                {
                    var tool = new SqlConnection(settings.ConnectionString);
                    try
                    {
                        await tool.OpenAsync(ct);
                        return tool;
                    }
                    catch
                    {
                        await tool.DisposeAsync();
                        throw;
                    }
                },
                () =>
                {
                    keptTunnel?.Dispose();
                    if (keptCertificate is not null)
                    {
                        try
                        {
                            File.Delete(keptCertificate);
                        }
                        catch (IOException)
                        {
                            // A temporary file of a public certificate: nothing to worry about if it stays.
                        }
                    }
                },
                timeProvider.GetUtcNow());
            if (!tunnel.IsTunnel)
            {
                tunnel.Dispose();
            }

            connection = null;
            certificateFile = null;
            return new DatabaseOpenResult(session, null, display);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            tunnel.Dispose();
            if (certificateFile is not null)
            {
                File.Delete(certificateFile);
            }

            throw;
        }
    }

    private static async Task<DatabaseServerInfo> ReadServerInfoAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CAST(@@SERVERNAME AS nvarchar(256)),
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   SUSER_SNAME(),
                   @@SPID
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DatabaseServerInfo(
            reader.IsDBNull(0) ? connection.DataSource : reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt16(5));
    }

    /// <summary>A failed sign-in, in words: SQL Server's message, coded for the browser.</summary>
    internal static SessionException SignInFailure(SqlException ex, string username, string display) => ex.Number switch
    {
        18456 => new SessionException(SessionErrorCodes.AuthenticationFailed, $"SQL Server refused the login “{username}”. Check the username and password, and that the server allows SQL Server authentication."),
        18452 => new SessionException(SessionErrorCodes.AuthenticationFailed, "The server only allows Windows logins, so SQL logins can't sign in. Enable SQL Server and Windows Authentication mode on it."),
        18486 or 18487 or 18488 => new SessionException(SessionErrorCodes.AuthenticationFailed, ex.Message),
        4060 => new SessionException(SessionErrorCodes.SqlError, ex.Message),
        _ => new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't sign in to {display}: {ex.Message}"),
    };
}
