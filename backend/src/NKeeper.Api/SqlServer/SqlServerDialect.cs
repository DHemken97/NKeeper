using System.Data.Common;
using Microsoft.Data.SqlClient;
using NKeeper.Api.Databases;

namespace NKeeper.Api.SqlServer;

/// <summary>SQL Server's query runner, object explorer, scripts and blocking manager.</summary>
internal sealed class SqlServerDialect : IDatabaseDialect
{
    public static readonly SqlServerDialect Instance = new();

    private static SqlConnection Sql(DbConnection connection) => (SqlConnection)connection;

    public Task<SqlQueryResult> RunAsync(DbConnection connection, string script, int lineOffset, CancellationToken cancellationToken) =>
        SqlQueryRunner.RunAsync(Sql(connection), script, lineOffset, cancellationToken);

    public Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken cancellationToken) => Task.FromResult(connection.Database);

    public async Task<IReadOnlyList<string>> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await SqlObjectExplorer.QueryAsync(
            Sql(connection),
            "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name",
            [],
            r => r.GetString(0),
            cancellationToken);

    public async Task<string> ChangeDatabaseAsync(DbConnection connection, string name, CancellationToken cancellationToken)
    {
        await connection.ChangeDatabaseAsync(name, cancellationToken);
        return connection.Database;
    }

    public Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken) =>
        SqlObjectExplorer.ChildrenAsync(Sql(connection), node, cancellationToken);

    public Task<string> ScriptAsync(DbConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken) =>
        SqlScripter.ScriptAsync(Sql(connection), node, action, cancellationToken);

    public Task<BlockingReport> BlockingAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
        SqlBlocking.ReadAsync(Sql(connection), now, cancellationToken);

    public Task KillAsync(DbConnection connection, int sessionId, int? serial, CancellationToken cancellationToken) =>
        SqlBlocking.KillAsync(Sql(connection), sessionId, cancellationToken);
}
