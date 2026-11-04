using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using NKeeper.Api.Access;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.SqlServer;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Databases;

/// <summary>The kinds of database server NKeeper opens query windows and an object explorer on.</summary>
public enum DatabaseEngine
{
    SqlServer,
    Oracle,
    Ase,

    /// <summary>SAP Replication Server (spoken to like ASE).</summary>
    RepServer,
}

internal static class DatabaseEngines
{
    /// <summary>The engine's path under <c>/api</c>: <c>mssql</c>, <c>oracle</c>, <c>ase</c>.</summary>
    public const string RoutePattern = "^(mssql|oracle|ase|rs)$";

    public static DatabaseEngine? FromRoute(string? route) => route switch
    {
        "mssql" => DatabaseEngine.SqlServer,
        "oracle" => DatabaseEngine.Oracle,
        "ase" => DatabaseEngine.Ase,
        "rs" => DatabaseEngine.RepServer,
        _ => null,
    };

    public static string Route(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Oracle => "oracle",
        DatabaseEngine.Ase => "ase",
        DatabaseEngine.RepServer => "rs",
        _ => "mssql",
    };

    /// <summary>As the Connect menu and the audit trail name it.</summary>
    public static string Label(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Oracle => "Oracle",
        DatabaseEngine.Ase => "SAP ASE",
        DatabaseEngine.RepServer => "Rep Server",
        _ => "MS SQL",
    };

    /// <summary>The product, for messages.</summary>
    public static string Product(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Oracle => "Oracle",
        DatabaseEngine.Ase => "SAP ASE",
        DatabaseEngine.RepServer => "Replication Server",
        _ => "SQL Server",
    };

    public static ServiceProtocol Protocol(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Oracle => ServiceProtocol.Oracle,
        DatabaseEngine.Ase => ServiceProtocol.Ase,
        DatabaseEngine.RepServer => ServiceProtocol.RepServer,
        _ => ServiceProtocol.Ssms,
    };

    public static IDatabaseDialect Dialect(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Oracle => Oracle.OracleDialect.Instance,
        DatabaseEngine.Ase => Ase.AseDialect.Instance,
        DatabaseEngine.RepServer => Ase.AseDialect.ReplicationServer,
        _ => SqlServerDialect.Instance,
    };
}

/// <summary>Opens a database session: the host's database service and a login.</summary>
public sealed record DatabaseOpenRequest : SessionStartMessage
{
    /// <summary>The server's certificate fingerprint the user chose to trust after a "trustRequired" answer (SQL Server).</summary>
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>The database (Oracle: schema) to start in; the login's default when empty.</summary>
    [StringLength(128)]
    public string? Database { get; init; }
}

/// <summary>The outcome of opening a session: the session, or the certificate the user must trust first.</summary>
/// <param name="Renewed">The prompt is for a new self-signed certificate SQL Server made in place of the trusted one.</param>
internal sealed record DatabaseOpenResult(DatabaseSession? Session, WebCertificate? Prompt, string Target, bool Renewed = false);

/// <summary>Opens sessions on one kind of database server.</summary>
internal interface IDatabaseConnector
{
    DatabaseEngine Engine { get; }

    /// <exception cref="SessionException">The server can't be reached or refused the login.</exception>
    Task<DatabaseOpenResult> OpenAsync(DatabaseOpenRequest request, RequestUser user, CancellationToken cancellationToken);
}

/// <summary>What differs between database servers once connected: running scripts, the explorer, scripts of objects, blocking.</summary>
internal interface IDatabaseDialect
{
    Task<SqlQueryResult> RunAsync(DbConnection connection, string script, int lineOffset, CancellationToken cancellationToken);

    /// <summary>The database (Oracle: schema) the session is in.</summary>
    Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Databases (Oracle: schemas) to choose from, for the toolbar.</summary>
    Task<IReadOnlyList<string>> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Switches the session to another database (Oracle: its current schema); returns the one it's in now.</summary>
    Task<string> ChangeDatabaseAsync(DbConnection connection, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken);

    Task<string> ScriptAsync(DbConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken);

    Task<BlockingReport> BlockingAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken);

    /// <param name="serial">Oracle's SERIAL# of the session (a SID alone can be reused).</param>
    Task KillAsync(DbConnection connection, int sessionId, int? serial, CancellationToken cancellationToken);
}

/// <summary>Reads a query's rows into a list.</summary>
internal static class DbQuery
{
    public static async Task<IReadOnlyList<T>> ListAsync<T>(
        DbConnection connection, string sql, IEnumerable<(string Name, object? Value)> parameters, Func<DbDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(map(reader));
        }

        return list;
    }

    public static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)?.Trim();

    public static int Int(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public static long? Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}
