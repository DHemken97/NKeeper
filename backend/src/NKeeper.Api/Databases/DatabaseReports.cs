using System.Data.Common;
using NKeeper.Api.Ase;
using NKeeper.Api.Oracle;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Databases;

/// <summary>
/// Every engine's Management reports, by the tool name the explorer gives them: one place to find what a report
/// runs, served by <c>GET api/{engine}/sessions/{id}/reports/{tool}</c>. Reports only read; what they offer to
/// change is a script the user runs.
/// </summary>
internal static class DatabaseReports
{
    public static Task<ToolReport> RunAsync(DatabaseEngine engine, string tool, DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
        (engine, tool) switch
        {
            (DatabaseEngine.Oracle, OracleDba.Storage or OracleDba.Backups or OracleDba.Health) => OracleDba.ReportAsync(connection, tool, now, cancellationToken),

            (DatabaseEngine.Ase, AseReplication.Tool) => AseReplication.PrimaryAsync(connection, now, cancellationToken),
            (DatabaseEngine.Ase, AseDba.Space) => AseDba.SpaceAsync(connection, now, cancellationToken),
            (DatabaseEngine.Ase, AseDba.Drift) => AseDba.DriftAsync(connection, now, cancellationToken),
            (DatabaseEngine.Ase, SybaseAdmin.License) => SybaseAdmin.AseLicenseAsync(connection, now, cancellationToken),
            (DatabaseEngine.Ase, AseHealth.Dumps or AseHealth.Errors or AseHealth.Resources or AseHealth.Security or AseHealth.Maintenance) =>
                AseHealth.ReportAsync(connection, tool, now, cancellationToken),

            (DatabaseEngine.RepServer, AseReplication.Tool) => AseReplication.ReplicationServerAsync(connection, now, cancellationToken),
            (DatabaseEngine.RepServer, SybaseAdmin.Admin) => SybaseAdmin.AdminAsync(connection, now, cancellationToken),
            (DatabaseEngine.RepServer, SybaseAdmin.License) => SybaseAdmin.ReplicationServerLicenseAsync(connection, now, cancellationToken),

            _ => throw new SqlExplorerException($"{DatabaseEngines.Product(engine)} has no “{tool}” report."),
        };
}
