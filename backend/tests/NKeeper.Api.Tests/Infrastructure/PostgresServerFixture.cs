using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.PostgresServerFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// Provides a real PostgreSQL server for integration tests, so migrations and queries are exercised
/// against the production database engine rather than a substitute.
/// <list type="bullet">
/// <item>If <c>NKEEPER_TEST_POSTGRES</c> is set, that server is used (the role needs CREATEDB).</item>
/// <item>Otherwise a disposable <c>postgres:17-alpine</c> container is started with Testcontainers.</item>
/// <item>If neither is available, dependent tests are reported as skipped — not passed.</item>
/// </list>
/// Each test gets its own uniquely named database, dropped when the run finishes.
/// </summary>
public sealed class PostgresServerFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "NKEEPER_TEST_POSTGRES";

    private readonly List<string> _databases = [];
    private PostgreSqlContainer? _container;
    private string? _serverConnectionString;

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _serverConnectionString = configured;
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }
#pragma warning disable CA1031 // Any failure here means "no database available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason =
                $"PostgreSQL integration tests need Docker (Testcontainers) or {ConnectionStringVariable}. " +
                $"Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Creates an empty database and returns its connection string; skips the test when no server exists.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_serverConnectionString is null)
        {
            Assert.Skip(UnavailableReason ?? "PostgreSQL is not available.");
        }

        var name = $"nkeeper_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_serverConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        lock (_databases)
        {
            _databases.Add(name);
        }

        return new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        if (_serverConnectionString is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        foreach (var name in _databases)
        {
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
