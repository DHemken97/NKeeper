using NKeeper.Api.Inventory.Scanning;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>Internal scans find Oracle databases by name, so they can be added straight from the scan.</summary>
public sealed class OracleScanTests
{
    private static string Output(params string[] oracle) => string.Join(
        "\n",
        ["@@hostname", "ora01", "@@listening", "LISTEN 0 128 0.0.0.0:1521 0.0.0.0:*", "LISTEN 0 128 0.0.0.0:22 0.0.0.0:*", "@@oracle", .. oracle, "@@end", ""]);

    [Fact]
    public void Offers_each_database_the_listener_serves_by_its_service_name()
    {
        var findings = InternalScan.Parse(Output(
            "oratab ORCL /u01/app/oracle/product/19c",
            "pmon ORCL",
            "listen 0.0.0.0:1521",
            "service ORCL",
            "service ORCLXDB",
            "service orclpdb1",
            "service 5a4cd42ef8c011a1e0639a00010a3724",
            "service SYS$BACKGROUND"));

        var oracle = findings.Ports.Where(p => p.Protocol == ServiceProtocol.Oracle).ToList();
        Assert.Equal(["ORCL", "orclpdb1"], oracle.Select(p => p.Instance));
        Assert.All(oracle, p => Assert.Equal(1521, p.Port));
        Assert.Contains(findings.Ports, p => p.Port == 22);
    }

    [Fact]
    public void Falls_back_to_the_instances_SIDs_when_the_listener_cannot_be_asked()
    {
        var findings = InternalScan.Parse(Output("oratab XE /u01/app/oracle/product/11.2.0/xe", "pmon XE"));

        var oracle = Assert.Single(findings.Ports, p => p.Protocol == ServiceProtocol.Oracle);
        Assert.Equal(("XE", 1521), (oracle.Instance, oracle.Port));
        Assert.Contains(findings.Notes, n => n.Contains("by SID (XE)", StringComparison.Ordinal));
    }

    [Fact]
    public void Leaves_port_1521_alone_without_Oracle_on_the_host()
    {
        var findings = InternalScan.Parse(Output());

        var port = Assert.Single(findings.Ports, p => p.Port == 1521);
        Assert.Equal(ServiceProtocol.Oracle, port.Protocol);
        Assert.Null(port.Instance);
    }
}
