using AdoNetCore.AseClient;
using NKeeper.Api.Ase;
using NKeeper.Api.Databases;

namespace NKeeper.Api.Tests;

/// <summary>
/// Every SAP ASE report, run against a real server: each section must read without an error. Set
/// NKEEPER_ASE_TEST to a connection string to run them (e.g. the datagrip/sybase:16.0 container:
/// "Data Source=localhost;Port=15000;Database=master;Uid=sa;Pwd=myPassword;Pooling=false"); skipped otherwise.
/// </summary>
public sealed class AseReportsLiveTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("NKEEPER_ASE_TEST");

    public static TheoryData<string> Reports() =>
    [
        AseReplication.Tool, AseDba.Space, AseDba.Drift, SybaseAdmin.License,
        AseHealth.Dumps, AseHealth.Errors, AseHealth.Resources, AseHealth.Security, AseHealth.Maintenance,
    ];

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Report_reads_every_section(string tool)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "Set NKEEPER_ASE_TEST to an ASE connection string to run this.");
        await using var connection = new AseConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var report = await DatabaseReports.RunAsync(DatabaseEngine.Ase, tool, connection, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var failed = report.Sections.Where(s => s.Error is not null).Select(s => $"{s.Title}: {s.Error}").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        foreach (var warning in report.Warnings)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"[{tool}] {warning}");
        }

        foreach (var section in report.Sections)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"[{tool}] {section.Title}: {ToolReports.Rows(section).Count()} rows in {section.Results.Count(r => r.Kind == "results")} result sets, " +
                $"{section.Results.Count(r => r.Kind == "message")} messages, {section.Scripts?.Count ?? 0} scripts");
        }
    }
}
