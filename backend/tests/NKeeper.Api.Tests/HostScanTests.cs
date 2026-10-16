using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory.Scanning;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;

namespace NKeeper.Api.Tests;

/// <summary>
/// Inventory scans: the internal scan's output parsing, an internal scan of a real SSH server (Alpine,
/// with busybox tools), and an external scan of that server's published port.
/// </summary>
public sealed class HostScanTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _client = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private const string UbuntuOutput = """

        @@hostname
        web01.corp.example
        @@os
        Ubuntu 24.04.1 LTS
        @@kernel
        Linux 6.8.0-45-generic x86_64
        @@connection
        10.0.0.9 51234 10.0.0.30 22
        @@addresses
        1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
        2: ens18    inet 10.0.0.30/24 brd 10.0.0.255 scope global ens18\       valid_lft forever preferred_lft forever
        3: docker0    inet 172.17.0.1/16 brd 172.17.255.255 scope global docker0\       valid_lft forever preferred_lft forever
        @@macs
        docker0 02:42:ac:11:00:01
        ens18 bc:24:11:aa:bb:cc
        lo 00:00:00:00:00:00
        @@route
        default via 10.0.0.1 dev ens18 proto dhcp src 10.0.0.30 metric 100
        @@listening
        LISTEN 0      4096       127.0.0.53%lo:53         0.0.0.0:*
        LISTEN 0      4096         0.0.0.0:22         0.0.0.0:*
        LISTEN 0      511          0.0.0.0:443        0.0.0.0:*
        LISTEN 0      200        127.0.0.1:5432       0.0.0.0:*
        LISTEN 0      4096            [::]:22            [::]:*
        LISTEN 0      4096               *:9090             *:*
        @@uidmin
        1000
        @@users
        root:x:0:0:root:/root:/bin/bash
        daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin
        sshd:x:110:65534::/run/sshd:/usr/sbin/nologin
        dana:x:1000:1000:Dana Admin,,,:/home/dana:/bin/bash
        backup-bot:x:1001:1001::/home/backup-bot:/usr/sbin/nologin
        nobody:x:65534:65534:nobody:/nonexistent:/bin/sh
        @@libvirt
        @@end
        """;

    [Fact]
    public void InternalOutput_IsParsed()
    {
        var findings = InternalScan.Parse(UbuntuOutput);

        Assert.Equal(("web01.corp.example", "10.0.0.30", "BC:24:11:AA:BB:CC", "Ubuntu 24.04.1 LTS"), (findings.Hostname, findings.IpAddress, findings.MacAddress, findings.OperatingSystem));
        // Loopback-only listeners (DNS stub, PostgreSQL) can't be reached, so they aren't services.
        Assert.Equal([22, 443, 9090], findings.Ports.Select(p => p.Port));
        Assert.Equal([ServiceProtocol.Ssh, ServiceProtocol.Https, ServiceProtocol.Https], findings.Ports.Select(p => p.Protocol));
        Assert.Equal(["root", "dana"], findings.Users.Select(u => u.Username));
        Assert.Equal("Dana Admin", findings.Users[1].FullName);
        Assert.False(findings.IsKvmHost);
    }

    [Fact]
    public void InternalOutput_FromNetstat_AndAKvmHost()
    {
        var output = UbuntuOutput
            .Replace("@@libvirt\n", "@@libvirt\nyes\n", StringComparison.Ordinal)
            .Replace(
                "LISTEN 0      4096         0.0.0.0:22         0.0.0.0:*",
                "Active Internet connections (only servers)\nProto Recv-Q Send-Q Local Address           Foreign Address         State\ntcp        0      0 0.0.0.0:5900            0.0.0.0:*               LISTEN\ntcp        0      0 0.0.0.0:3389            0.0.0.0:*               LISTEN",
                StringComparison.Ordinal);

        var findings = InternalScan.Parse(output);

        Assert.True(findings.IsKvmHost);
        Assert.Contains(findings.Ports, p => p.Port == 3389 && p.Protocol == ServiceProtocol.Rdp);
        // On a KVM host, 5900+ are VM consoles, not the host's own VNC.
        Assert.DoesNotContain(findings.Ports, p => p.Port == 5900);
    }

    [Fact]
    public void HostsWithoutAPosixShell_AreExplained()
    {
        var error = Assert.Throws<SessionException>(() => InternalScan.Parse("'sh' is not recognized as an internal or external command"));
        Assert.Contains("Windows isn't supported yet", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5", "Ubuntu")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "Debian")]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", "Windows")]
    [InlineData("SSH-2.0-OpenSSH_9.8", null)]
    public void OsIsGuessedFromSshBanners(string banner, string? os) => Assert.Equal(os, ExternalScan.OsFromSshBanner(banner));

    [Fact]
    public void MacAddresses_AreReadFromTheArpTable()
    {
        string[] arp =
        [
            "IP address       HW type     Flags       HW address            Mask     Device",
            "10.0.0.1         0x1         0x2         aa:bb:cc:00:00:01     *        eth0",
            "10.0.0.7         0x1         0x0         00:00:00:00:00:00     *        eth0",
        ];

        Assert.Equal("AA:BB:CC:00:00:01", NeighborTable.FromProcArp(arp, IPAddress.Parse("10.0.0.1")));
        Assert.Null(NeighborTable.FromProcArp(arp, IPAddress.Parse("10.0.0.7")));
    }

    [Fact]
    public async Task InternalScan_ReadsARealServer_OverOneSshLogin()
    {
        ssh.SkipIfUnavailable();
        var host = await CreateHostAsync();

        var first = await ScanAsync(host, "internal", new { serviceIndex = 0, credentialId = host.Credentials[0].Id });
        Assert.Equal("trustRequired", first.Status);

        var scan = await ScanAsync(host, "internal", new { serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = first.HostKey!.Fingerprint });

        Assert.Equal("ready", scan.Status);
        var findings = scan.Findings!;
        Assert.Contains("Alpine", findings.OperatingSystem, StringComparison.Ordinal);
        Assert.NotNull(findings.Hostname);
        Assert.NotNull(findings.MacAddress);
        // sshd listens on 2222 in the container; the fake virsh makes it look like a KVM host.
        Assert.Contains(findings.Ports, p => p.Port == 2222);
        Assert.True(findings.IsKvmHost);
        Assert.Contains(findings.Users, u => u.Username == "root");
    }

    [Fact]
    public async Task Connections_UseTheIpAddress_OverTheHostname()
    {
        ssh.SkipIfUnavailable();
        // The hostname can never resolve (.invalid), so connecting at all proves the IP was used.
        var host = await CreateHostAsync(hostname: "never-resolves.invalid");

        var first = await ScanAsync(host, "internal", new { serviceIndex = 0, credentialId = host.Credentials[0].Id });

        Assert.Equal("trustRequired", first.Status);
    }

    [Fact]
    public async Task ExternalScan_FindsTheOpenPort_AndReadsTheSshBanner()
    {
        ssh.SkipIfUnavailable();
        var host = await CreateHostAsync();

        var scan = await ScanAsync(host, "external", new { });

        Assert.Equal("ready", scan.Status);
        Assert.Contains(scan.Findings!.Ports, p => p.Port == ssh.Port);
        Assert.Contains(scan.Findings.Notes, n => n.StartsWith($"SSH on port {ssh.Port} says: SSH-2.0-OpenSSH", StringComparison.Ordinal));
        Assert.Empty(scan.Findings.Users);
    }

    [Fact]
    public async Task Scans_NeedManageAccess()
    {
        var unknown = await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{Guid.NewGuid()}/scan/external", new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var host = await CreateHostAsync();
        await _client.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{host.Id}/scan/external", new { }, Ct)).StatusCode);
    }

    private async Task<ScanResponse> ScanAsync(HostResponse host, string kind, object body)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, $"/api/hosts/{host.Id}/scan/{kind}", body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScanResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateHostAsync(string? hostname = null)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"scan-{Guid.NewGuid():N}",
            hostname,
            ipAddress = SshServerFixture.Address,
            services = new[] { new { protocol = "Ssh", port = (int?)(ssh.UnavailableReason is null ? ssh.Port : 22) } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }
}
