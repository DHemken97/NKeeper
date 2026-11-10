using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Tests;

/// <summary>The insurance questionnaire's survey wording that doesn't need a server: schedules, versions, frequencies, places.</summary>
public sealed class SurveyLogicTests
{
    [Theory]
    [InlineData(4, 1, 1, 0, 0, 0, 10000, "daily, at 01:00")]
    [InlineData(4, 2, 1, 0, 0, 0, 233000, "every 2 days, at 23:30")]
    [InlineData(4, 1, 4, 15, 0, 0, 0, "daily, every 15 minutes from 00:00")]
    [InlineData(8, 62, 1, 0, 0, 1, 200000, "weekly on weekdays, at 20:00")]
    [InlineData(8, 1, 1, 0, 0, 2, 30000, "every 2 weeks on Sunday, at 03:00")]
    [InlineData(8, 65, 1, 0, 0, 1, 0, "weekly on Sunday, Saturday, at 00:00")]
    [InlineData(16, 1, 1, 0, 0, 1, 0, "every month on day 1, at 00:00")]
    [InlineData(32, 1, 1, 0, 16, 1, 220000, "every month on the last Sunday, at 22:00")]
    [InlineData(64, 0, 0, 0, 0, 0, 0, "when SQL Server Agent starts")]
    public void Agent_schedules_are_described_in_words(int type, int interval, int subdayType, int subdayInterval, int relative, int recurrence, int start, string expected) =>
        Assert.Equal(expected, SqlSurvey.DescribeSchedule(type, interval, subdayType, subdayInterval, relative, recurrence, start));

    [Theory]
    [InlineData("16.0.4135.4", "SQL Server 2022")]
    [InlineData("15.0.2000.5", "SQL Server 2019")]
    [InlineData("10.50.6000.34", "SQL Server 2008 R2")]
    [InlineData("10.0.6000.29", "SQL Server 2008")]
    [InlineData(null, null)]
    public void Sql_server_versions_have_their_release_names(string? version, string? expected) =>
        Assert.Equal(expected, SqlSurvey.ReleaseName(version));

    [Fact]
    public void Frequencies_folders_and_remote_places_read_naturally()
    {
        Assert.Equal("about every day", SurveyBuilder.Frequency(35, 35));
        Assert.Equal("about every week", SurveyBuilder.Frequency(5, 35));
        Assert.Equal("about 24 a day", SurveyBuilder.Frequency(840, 35));
        Assert.Equal("none in the last 35 days", SurveyBuilder.Frequency(0, 35));

        Assert.Equal(@"D:\Backups\Sales", SurveyBuilder.Folder(@"D:\Backups\Sales\Sales_full.bak"));
        Assert.Equal("/u01/fra", SurveyBuilder.Folder("/u01/fra/o1_mf_nnndf.bkp"));

        Assert.Equal("a network share on nas01", SurveyBuilder.Remote(@"\\nas01\sql\Sales.bak"));
        Assert.Equal("Azure Blob Storage", SurveyBuilder.Remote("https://acme.blob.core.windows.net/sql/Sales.bak"));
        Assert.Equal("Amazon S3 (or S3-compatible storage)", SurveyBuilder.Remote("s3://bucket/sql/Sales.bak"));
        Assert.Null(SurveyBuilder.Remote(@"D:\Backups\Sales.bak"));

        Assert.Equal("a, b and 2 more", SurveyBuilder.List(["a", "b", "c", "A", "d"], 2));
    }
}
