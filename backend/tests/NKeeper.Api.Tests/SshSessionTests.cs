using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Sdk.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Terminal sessions over WebSocket against a real OpenSSH server and PostgreSQL.</summary>
public sealed class SshSessionTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        ssh.SkipIfUnavailable();
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        (_client, _cookies) = await SignedInCookies.SignInAsync(_factory, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task FirstConnection_AsksToTrustTheHostKey_ThenRunsCommands()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = SshServerFixture.Password });

        await using (var terminal = await StartAsync(host, credentialIndex: 0))
        {
            Assert.Equal($"{SshServerFixture.UserName}@{SshServerFixture.Address}:{ssh.Port}", (await terminal.ExpectAsync("status", Ct)).GetProperty("target").GetString());
            var hostKey = await terminal.ExpectAsync("hostKey", Ct);
            Assert.Equal("unknown", hostKey.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(hostKey.GetProperty("fingerprint").GetString()));

            await terminal.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
            await terminal.ExpectAsync("connected", Ct);
            await terminal.SendInputAsync("echo nkeeper-$((40+2))\n", Ct);
            await terminal.WaitForOutputAsync("nkeeper-42", Ct);
        }

        var stored = await GetHostAsync(host.Id);
        var key = Assert.Single(stored.TrustedKeys);
        Assert.Equal(ssh.Port, key.Port);

        // The trusted key is not asked about again.
        await using var second = await StartAsync(host, credentialIndex: 0);
        await second.ExpectAsync("status", Ct);
        await second.ExpectAsync("connected", Ct);
    }

    [Fact]
    public async Task RejectingTheHostKey_EndsTheSessionAndTrustsNothing()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = SshServerFixture.Password });

        await using var terminal = await StartAsync(host, credentialIndex: 0);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("hostKey", Ct);
        await terminal.SendJsonAsync(new { type = "hostKeyDecision", trust = false }, Ct);

        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Equal(SessionErrorCodes.HostKeyRejected, error.GetProperty("code").GetString());
        Assert.Empty((await GetHostAsync(host.Id)).TrustedKeys);
    }

    [Fact]
    public async Task ChangedHostKey_IsRefused()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = SshServerFixture.Password });
        await TrustHostKeyAsync(host);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().HostTrustedKeys
                .ExecuteUpdateAsync(k => k.SetProperty(x => x.Fingerprint, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), Ct);
        }

        await using var terminal = await StartAsync(host, credentialIndex: 0);
        await terminal.ExpectAsync("status", Ct);
        var hostKey = await terminal.ExpectAsync("hostKey", Ct);
        var error = await terminal.ExpectAsync("error", Ct);

        Assert.Equal("changed", hostKey.GetProperty("status").GetString());
        Assert.Equal(SessionErrorCodes.HostKeyChanged, error.GetProperty("code").GetString());
        Assert.Contains("intercepting", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgettingHostKeys_MakesTheNextConnectionAskAgain()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = SshServerFixture.Password });
        await TrustHostKeyAsync(host);

        var response = await _client.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}/trusted-keys", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await GetHostAsync(host.Id)).TrustedKeys);
    }

    [Fact]
    public async Task PrivateKeyCredential_Connects()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, privateKey = ssh.PrivateKey });
        await TrustHostKeyAsync(host);

        await using var terminal = await StartAsync(host, credentialIndex: 0);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("connected", Ct);
        await terminal.SendInputAsync("whoami\n", Ct);
        await terminal.WaitForOutputAsync(SshServerFixture.UserName, Ct);
    }

    [Fact]
    public async Task OneOffCredentials_Connect_AndResizeReachesTheShell()
    {
        var host = await CreateHostAsync(credential: null);
        await TrustHostKeyAsync(host);

        await using var terminal = await StartAsync(host, credentialIndex: null);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("connected", Ct);
        await terminal.SendJsonAsync(new { type = "resize", cols = 132, rows = 43 }, Ct);
        await terminal.SendInputAsync("stty size\n", Ct);
        await terminal.WaitForOutputAsync("43 132", Ct);
    }

    [Fact]
    public async Task WrongPassword_ReportsAuthenticationFailure()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = "wrong-password" });
        await TrustHostKeyAsync(host);

        await using var terminal = await StartAsync(host, credentialIndex: 0);
        await terminal.ExpectAsync("status", Ct);

        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Equal(SessionErrorCodes.AuthenticationFailed, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NonSshService_IsRejectedBeforeConnecting()
    {
        var host = await CreateHostAsync(new { username = "u", password = "p" }, protocol: "Rdp");

        await using var terminal = await StartAsync(host, credentialIndex: 0);

        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Equal(SessionErrorCodes.WrongService, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CredentialOfAnotherHost_IsRejected()
    {
        var other = await CreateHostAsync(new { username = "u", password = "p" });
        var host = await CreateHostAsync(credential: null);

        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct);
        await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId = other.Credentials[0].Id, cols = 80, rows = 24 }, Ct);

        var error = await terminal.ExpectAsync("error", Ct);
        Assert.Equal(SessionErrorCodes.CredentialNotFound, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CrossSiteOrigin_IsRefused()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => TerminalClient.ConnectAsync(_factory, _cookies, Ct, origin: "https://evil.example"));
    }

    [Fact]
    public async Task WithoutSession_IsRefused()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => TerminalClient.ConnectAsync(_factory, new CookieContainer(), Ct));
    }

    [Fact]
    public async Task ChangingTheHostAddress_ForgetsTrustedKeys()
    {
        var host = await CreateHostAsync(new { username = SshServerFixture.UserName, password = SshServerFixture.Password });
        await TrustHostKeyAsync(host);
        var current = await GetHostAsync(host.Id);

        var response = await _client.SendJsonAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new
        {
            name = current.Name,
            ipAddress = "127.0.0.2",
            services = new[] { new { protocol = "Ssh", port = ssh.Port } },
            version = current.Version,
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!.TrustedKeys);
    }

    private async Task<HostResponse> CreateHostAsync(object? credential, string protocol = "Ssh")
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"ssh-{Guid.NewGuid():N}",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { protocol, port = (int?)ssh.Port } },
            credentials = credential is null ? Array.Empty<object>() : [credential],
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> GetHostAsync(Guid id) =>
        (await _client.GetFromJsonAsync<HostResponse>($"/api/hosts/{id}", Json, Ct))!;

    /// <summary>Starts a session with the host's credential, or with one-off credentials when the index is null.</summary>
    private async Task<TerminalClient> StartAsync(HostResponse host, int? credentialIndex)
    {
        var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct);
        object start = credentialIndex is { } index
            ? new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[index].Id, cols = 80, rows = 24 }
            : new { type = "start", hostId = host.Id, serviceIndex = 0, username = SshServerFixture.UserName, password = SshServerFixture.Password, cols = 80, rows = 24 };
        await terminal.SendJsonAsync(start, Ct);
        return terminal;
    }

    /// <summary>Connects once with the test password and trusts the server's key.</summary>
    private async Task TrustHostKeyAsync(HostResponse host)
    {
        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct);
        await terminal.SendJsonAsync(
            new { type = "start", hostId = host.Id, serviceIndex = 0, username = SshServerFixture.UserName, password = SshServerFixture.Password, cols = 80, rows = 24 },
            Ct);
        await terminal.ExpectAsync("status", Ct);
        await terminal.ExpectAsync("hostKey", Ct);
        await terminal.SendJsonAsync(new { type = "hostKeyDecision", trust = true }, Ct);
        await terminal.ExpectAsync("connected", Ct);
    }
}
