using NKeeper.Api.Ase;
using NKeeper.Api.Databases;
using NKeeper.Api.Oracle;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Tests;

/// <summary>The Oracle and SAP ASE tools' logic that doesn't need a server: copy steps, versions, file names, licenses.</summary>
public sealed class DatabaseToolLogicTests
{
    private static AseCopyStepRequest Step(string step) => new()
    {
        TargetSessionId = "t",
        Database = "sales",
        TargetDatabase = "sales_copy",
        Step = step,
        DumpPath = "/dumps/sales.dmp",
        Compress = true,
    };

    [Fact]
    public void Copy_steps_run_where_they_belong_with_checked_names_and_paths()
    {
        Assert.Equal((true, "dump database [sales] to '/dumps/sales.dmp' at TGT_BS with compression = 101"),
            Single(AseCopy.Plan(Step("dump") with { RemoteBackupServer = "TGT_BS" }, ["TGT_BS"])));
        Assert.Equal((false, "load database [sales_copy] from '/dumps/sales.dmp'"), Single(AseCopy.Plan(Step("load"), [])));
        Assert.Equal((false, "online database [sales_copy]"), Single(AseCopy.Plan(Step("online"), [])));
        Assert.Equal(["use [sales_copy]", "dbcc settrunc('ltm', 'ignore')", "use master"], AseCopy.Plan(Step("settrunc"), []).Statements);

        static (bool, string) Single((bool OnSource, string Title, string[] Statements) plan) => (plan.OnSource, Assert.Single(plan.Statements));
    }

    [Theory]
    [InlineData("dump", "sales]; drop database master --", null, null)]
    [InlineData("load", "sales", "/dumps/x'; shutdown --", null)]
    [InlineData("dump", "sales", "/dumps/sales.dmp", "UNKNOWN_BS")]
    [InlineData("dump", "sales", "/dumps/sales.dmp", "bad name; shutdown")]
    [InlineData("vacuum", "sales", "/dumps/sales.dmp", null)]
    public void Copy_steps_refuse_what_isnt_a_name_path_or_known_server(string step, string database, string? path, string? remote)
    {
        var request = Step(step) with { Database = database, DumpPath = path, RemoteBackupServer = remote };
        Assert.Throws<SqlExplorerException>(() => AseCopy.Plan(request, ["TGT_BS"]));
    }

    [Theory]
    [InlineData("create database [sales_copy] on data1 = '500M' log on log1 = '100M' for load", true)]
    [InlineData("create database sales_copy on data1 = '500M' for load", true)]
    [InlineData("create database [other] on data1 = '500M' for load", false)]
    [InlineData("create database [sales_copy] on data1 = '500M'", false)]
    [InlineData("create database [sales_copy] on data1 = '500M' for load; shutdown", false)]
    [InlineData("drop database sales_copy", false)]
    public void The_create_step_runs_only_a_create_for_load_of_the_target(string sql, bool accepted)
    {
        var request = Step("create") with { CreateSql = sql };
        if (accepted)
        {
            Assert.Equal(sql, Assert.Single(AseCopy.Plan(request, []).Statements));
        }
        else
        {
            Assert.Throws<SqlExplorerException>(() => AseCopy.Plan(request, []));
        }
    }

    [Theory]
    [InlineData("16.0 SP04 PL01", "16.0 SP03 PL08", 1)]
    [InlineData("16.0 SP03 PL08", "16.0 SP03 PL08", 0)]
    [InlineData("15.7 SP140", "16.0", -1)]
    [InlineData("16.0", "16.0 SP01", -1)]
    public void Versions_compare_by_release_service_pack_and_patch(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(AseCopy.VersionKey(a).CompareTo(AseCopy.VersionKey(b))));

    [Theory]
    [InlineData("/u01/oradata/ORCL/users01.dbf", "USERS", 2, "/u01/oradata/ORCL/users02.dbf")]
    [InlineData(@"D:\oradata\ORCL\USERS01.DBF", "USERS", 3, @"D:\oradata\ORCL\users03.dbf")]
    [InlineData("+DATA/ORCL/DATAFILE/users.259.1100000000", "USERS", 2, "+DATA")]
    public void A_new_datafile_goes_beside_the_last_one_or_in_its_disk_group(string last, string tablespace, int number, string expected) =>
        Assert.Equal(expected, OracleDba.NewFilePath(last, tablespace, number));

    [Fact]
    public void Licenses_warn_in_their_grace_period_and_before_they_expire()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<SqlColumn> mon = [new("Name", "varchar", false), new("Status", "varchar", false), new("LicenseExpiry", "datetime", false), new("GraceExpiry", "datetime", false)];
        IReadOnlyList<SqlColumn> lm = [new("License Name", "varchar", false), new("Status", "varchar", false), new("Expiry Date", "varchar", false)];
        var sections = new[]
        {
            new ToolSection("Licenses", "monLicense",
            [
                new SqlOutput("results", Columns: mon, Rows:
                [
                    ["ASE_CORE", "graced", null, "2026-11-20 00:00:00.000"],
                    ["ASE_PARTITIONS", "OK", "2026-11-14 00:00:00.000", null],
                    ["ASE_ENCRYPTION", "OK", "2027-06-01 00:00:00.000", null],
                ]),
            ], null),
            new ToolSection("SySAM", "sp_lmconfig", [new SqlOutput("results", Columns: lm, Rows: [["ASE_CORE", "OK", "Permanent"]])], null),
        };

        var warnings = SybaseAdmin.LicenseWarnings(sections, now);

        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("ASE_CORE is running in its grace period, which ends 2026-11-20", warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("ASE_PARTITIONS's license expires 2026-11-14", warnings[1], StringComparison.Ordinal);
    }
}
