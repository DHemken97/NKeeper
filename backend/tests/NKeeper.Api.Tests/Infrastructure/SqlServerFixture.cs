using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.SqlServerFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A real SQL Server 2022 (mcr.microsoft.com/mssql/server in Docker), with its self-signed
/// certificate, for SQL Server session tests. Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Address = "127.0.0.1";
    public const string UserName = "sa";
    public const string Password = "Sql-Test-Passw0rd!";

    private MsSqlContainer? _container;

    public string? UnavailableReason { get; private set; }

    public int Port => _container!.GetMappedPublicPort(MsSqlBuilder.MsSqlPort);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithPassword(Password).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no SQL Server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"SQL Server tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_container is null || UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason ?? "SQL Server is not available.");
        }
    }

    /// <summary>A connection of the test's own (as sa), to set things up or to hold locks.</summary>
    public async Task<SqlConnection> OpenAsync(string database = "master", CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{Address},{Port}",
            UserID = UserName,
            Password = Password,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs a shell script in the server's container (as its service account), returning what it printed.</summary>
    public async Task<string> RunShellAsync(string script)
    {
        var result = await _container!.ExecAsync(["sh", "-c", script]);
        return result.Stdout;
    }

    public async Task ExecuteAsync(string sql, string database = "master", CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(database, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
