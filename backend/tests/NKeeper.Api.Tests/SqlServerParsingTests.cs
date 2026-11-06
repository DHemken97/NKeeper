using System.Text;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Tests;

/// <summary>The parts of SQL Server sessions that need no server: GO batches, blocking chains, the SQL Server Browser's answer.</summary>
public sealed class SqlServerParsingTests
{
    [Fact]
    public void Batches_SplitAtGoLines_WithTheirLinesAndCounts()
    {
        var batches = SqlBatches.Split("SELECT 1\r\ngo\nPRINT 'a'\n\n  GO 3 -- three times\nSELECT 2\nGO\n\nGO\n");

        Assert.Equal(
            [("SELECT 1", 1, 1), ("PRINT 'a'", 3, 3), ("SELECT 2", 6, 1)],
            batches.Select(b => (b.Text, b.StartLine, b.Count)));
    }

    [Fact]
    public void Batches_IgnoreGoInsideStringsCommentsAndQuotedNames()
    {
        var script = "SELECT 'it''s\nGO\nstill a string'\n/* a /* nested */ comment\nGO\n*/\nSELECT [odd\nGO\nname]\nGO\nSELECT 2 -- GO\nGOTO label";

        var batches = SqlBatches.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("SELECT 'it''s", batches[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("name]", batches[0].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 2 -- GO\nGOTO label", batches[1].Text);
        Assert.Equal(11, batches[1].StartLine);
    }

    [Fact]
    public void Batches_ClampRepeatCounts()
    {
        Assert.Equal(SqlBatches.MaxCount, Assert.Single(SqlBatches.Split("SELECT 1\nGO 999999")).Count);
        Assert.Empty(SqlBatches.Split("\n  \nGO\n"));
    }

    [Fact]
    public void Chains_GroupBlockedSessionsUnderTheirLeadBlocker_WithDirectAndTotalCounts()
    {
        BlockingSession[] sessions =
        [
            Session(52, blockedBy: null),
            Session(60, blockedBy: 52, waitMs: 100),
            Session(61, blockedBy: 52, waitMs: 900),
            Session(70, blockedBy: 61),
            Session(71, blockedBy: 70),
            Session(80, blockedBy: null),
            Session(81, blockedBy: 80),
        ];

        var leads = SqlBlocking.BuildChains(sessions);

        Assert.Equal([52, 80], leads.Select(l => l.SessionId));
        var lead = leads[0];
        Assert.Equal(2, lead.Blocked);
        Assert.Equal(4, lead.BlockedVolume);
        // Longest wait first.
        Assert.Equal([61, 60], lead.BlockedSessions.Select(s => s.SessionId));
        Assert.Equal(2, lead.BlockedSessions[0].BlockedVolume);
        Assert.Equal(71, lead.BlockedSessions[0].BlockedSessions[0].BlockedSessions[0].SessionId);
        Assert.Equal((1, 1), (leads[1].Blocked, leads[1].BlockedVolume));
    }

    [Fact]
    public void Chains_ShowBlockersThatArentSessions_AndCycles()
    {
        var leads = SqlBlocking.BuildChains([Session(60, blockedBy: -2), Session(90, blockedBy: 91), Session(91, blockedBy: 90)]);

        var orphan = leads.Single(l => l.SessionId == -2);
        Assert.True(orphan.Placeholder);
        Assert.Equal("orphaned distributed transaction", orphan.Status);
        Assert.Equal(60, Assert.Single(orphan.BlockedSessions).SessionId);

        var cycle = leads.Single(l => l.SessionId == 90);
        Assert.Null(cycle.BlockedBy);
        Assert.Equal(91, Assert.Single(cycle.BlockedSessions).SessionId);
        Assert.Empty(cycle.BlockedSessions[0].BlockedSessions);
    }

    [Fact]
    public void Chains_ListASessionBlockingItself_EvenWithNoChain()
    {
        var onlyItself = SqlBlocking.BuildChains([Session(70, blockedBy: 70), Session(71, blockedBy: null) with { SelfBlocking = true }, Session(72, blockedBy: null)]);

        // A session that blocks no one, and isn't blocking itself, still isn't listed.
        Assert.Equal([70, 71], onlyItself.Select(l => l.SessionId));
        Assert.All(onlyItself, lead =>
        {
            Assert.True(lead.SelfBlocking);
            Assert.Null(lead.BlockedBy);
            Assert.Equal((0, 0), (lead.Blocked, lead.BlockedVolume));
            Assert.Empty(lead.BlockedSessions);
        });

        // It keeps its place in a chain, whichever end it's at.
        var chain = Assert.Single(SqlBlocking.BuildChains([Session(80, blockedBy: 80), Session(81, blockedBy: 80), Session(82, blockedBy: 81) with { SelfBlocking = true }]));
        Assert.Equal(80, chain.SessionId);
        Assert.True(chain.SelfBlocking);
        Assert.Equal(2, chain.BlockedVolume);
        var inner = Assert.Single(Assert.Single(chain.BlockedSessions).BlockedSessions);
        Assert.Equal((82, 81, true), (inner.SessionId, inner.BlockedBy, inner.SelfBlocking));
    }

    [Fact]
    public void Browser_ReadsTheInstancesTcpPort()
    {
        static byte[] Answer(string text)
        {
            var body = Encoding.ASCII.GetBytes(text);
            return [0x05, (byte)body.Length, 0, .. body];
        }

        Assert.Equal(49172, SqlServerBrowser.ReadTcpPort(Answer("ServerName;SQL01;InstanceName;SQLEXPRESS;IsClustered;No;Version;16.0.1000.6;tcp;49172;;")));
        Assert.Null(SqlServerBrowser.ReadTcpPort(Answer("ServerName;SQL01;InstanceName;SQLEXPRESS;IsClustered;No;Version;16.0.1000.6;np;\\\\SQL01\\pipe\\sql\\query;;")));
        Assert.Null(SqlServerBrowser.ReadTcpPort([0x04, 0, 0]));
    }

    private static BlockingSession Session(int id, int? blockedBy, long? waitMs = null) =>
        new(id, "sa", "app01", "app", "db", blockedBy is null ? "sleeping" : "suspended", blockedBy is null ? 1 : 0, blockedBy, null, blockedBy is null ? null : "LCK_M_X", waitMs, null, null, 0, 0, []);
}
