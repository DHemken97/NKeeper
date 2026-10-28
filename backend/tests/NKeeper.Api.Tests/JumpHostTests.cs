using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>
/// Hosts reached through a jump host. The OpenSSH container is the jump host; the "remote" host is
/// the container's own loopback (its sshd on 2222, its echo server), which only the jump host can reach.
/// </summary>
public sealed class JumpHostTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private const int InnerSshPort = 2222;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        ssh.SkipIfUnavailable();
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        (_admin, _cookies) = await SignedInCookies.SignInAsync(_factory, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _admin?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task SshSessions_GoThroughTheJumpHost_AndStillCheckTheTargetsOwnKey()
    {
        var jump = await CreateJumpHostAsync(trusted: true);
        var target = await CreateAsync(new
        {
            name = "inner",
            ipAddress = "127.0.0.1",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Ssh", port = InnerSshPort } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        });
        Assert.Equal(jump.Id, target.JumpHostId);

        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct);
        await terminal.SendJsonAsync(new { type = "start", hostId = target.Id, serviceIndex = 0, credentialId = target.Credentials[0].Id }, Ct);
        await terminal.ExpectAsync("status", Ct);
        // The target's key is asked about for the target, not taken from the jump host.
        Assert.Equal(InnerSshPort, (await terminal.ExpectAsync("hostKey", Ct)).GetProperty("port").GetInt32());
        await terminal.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
        await terminal.ExpectAsync("connected", Ct);
        await terminal.SendInputAsync("echo through-the-jump-$((6*7))\n", Ct);
        await terminal.WaitForOutputAsync("through-the-jump-42", Ct);
    }

    [Fact]
    public async Task Telnet_GoesThroughTheJumpHost_ToWhatOnlyItCanReach()
    {
        var jump = await CreateJumpHostAsync(trusted: true);
        var target = await CreateAsync(new
        {
            name = "echo",
            ipAddress = "127.0.0.1",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Telnet", port = SshServerFixture.FakeSpicePort } },
        });

        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "telnet");
        await terminal.SendJsonAsync(new { type = "start", hostId = target.Id, serviceIndex = 0 }, Ct);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("connected", Ct);
        await terminal.SendInputAsync("ping-through-jump\r", Ct);
        await terminal.WaitForOutputAsync("ping-through-jump", Ct);
    }

    [Fact]
    public async Task AJumpHostWhoseKeyIsntTrusted_IsRefused_WithTheWayForward()
    {
        var jump = await CreateJumpHostAsync(trusted: false);
        var target = await CreateAsync(new
        {
            name = "inner",
            ipAddress = "127.0.0.1",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Ssh", port = InnerSshPort } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        });

        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct);
        await terminal.SendJsonAsync(new { type = "start", hostId = target.Id, serviceIndex = 0, credentialId = target.Credentials[0].Id }, Ct);
        await terminal.ExpectAsync("status", Ct);
        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Contains("Connect to it over SSH once", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopSessions_ThroughAJumpHost_AreRefusedClearly()
    {
        var jump = await CreateJumpHostAsync(trusted: true);
        var target = await CreateAsync(new
        {
            name = "desktop",
            ipAddress = "127.0.0.1",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Vnc" } },
            credentials = new[] { new { password = "x", label = "VNC" } },
        });

        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "vnc");
        await terminal.SendJsonAsync(new { type = "start", hostId = target.Id, serviceIndex = 0, credentialId = target.Credentials[0].Id, width = 800, height = 600, dpi = 96 }, Ct);
        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Contains("can't go through a jump host yet", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsingAJumpHost_NeedsAccessToItToo()
    {
        var jump = await CreateJumpHostAsync(trusted: true);
        var target = await CreateAsync(new
        {
            name = "inner",
            ipAddress = "127.0.0.1",
            jumpHostId = jump.Id,
            services = new[] { new { protocol = "Ssh", port = InnerSshPort } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        });
        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        var users = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/users", Ct);
        var bobId = users!.Single(u => u.GetProperty("userName").GetString() == "bob").GetProperty("id").GetString();
        await _admin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new { grants = new[] { new { hostId = target.Id, level = "Use" } } }, Ct);

        var bobCookies = new CookieContainer();
        using var bob = _factory.CreateDefaultClient(new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(bobCookies));
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        await using var terminal = await TerminalClient.ConnectAsync(_factory, bobCookies, Ct);
        await terminal.SendJsonAsync(new { type = "start", hostId = target.Id, serviceIndex = 0, credentialId = target.Credentials[0].Id }, Ct);
        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Contains("jump host you don't have access to", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JumpHosts_MustBeDirectlyReachableSshHosts()
    {
        var noSsh = await CreateAsync(new { name = "web", ipAddress = "10.0.0.2", services = new[] { new { protocol = "Https" } } });
        var bad = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "x", ipAddress = "10.0.0.3", jumpHostId = noSsh.Id }, Ct);
        Assert.Contains("needs an SSH service", await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var jump = await CreateJumpHostAsync(trusted: false);
        var behind = await CreateAsync(new { name = "behind", ipAddress = "10.0.0.4", jumpHostId = jump.Id, services = new[] { new { protocol = "Ssh" } } });
        var chained = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "y", ipAddress = "10.0.0.5", jumpHostId = behind.Id }, Ct);
        Assert.Contains("itself reached through a jump host", await chained.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var foreign = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "z", ipAddress = "10.0.0.6", jumpHostId = jump.Id, jumpCredentialId = behind.Id }, Ct);
        Assert.Contains("isn't one of the jump host's", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The OpenSSH container as a jump host; "trusted" first trusts its key with an SFTP listing.</summary>
    private async Task<HostResponse> CreateJumpHostAsync(bool trusted)
    {
        var jump = await CreateAsync(new
        {
            name = $"bastion-{Guid.NewGuid():N}"[..20],
            ipAddress = SshServerFixture.Address,
            services = new[] { new { protocol = "Ssh", port = ssh.Port } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        });
        if (trusted)
        {
            var first = await (await _admin.SendJsonAsync(HttpMethod.Post, "/api/sftp/list", new { hostId = jump.Id, serviceIndex = 0, credentialId = jump.Credentials[0].Id }, Ct))
                .Content.ReadFromJsonAsync<JsonElement>(Ct);
            var fingerprint = first.GetProperty("hostKey").GetProperty("fingerprint").GetString();
            var ready = await _admin.SendJsonAsync(HttpMethod.Post, "/api/sftp/list", new { hostId = jump.Id, serviceIndex = 0, credentialId = jump.Credentials[0].Id, trustFingerprint = fingerprint }, Ct);
            Assert.Equal("ready", (await ready.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        }

        return jump;
    }

    private async Task<HostResponse> CreateAsync(object host)
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", host, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }
}
