using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Access;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Api.Transfer;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>
/// Moving a whole instance: export from one NKeeper (its own database, so its own Data Protection
/// keys) and import into a fresh one. Secrets must survive re-encryption, and nothing must leak.
/// </summary>
public sealed class TransferTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple 42";
    private const string TargetAdminPassword = "Target-Admin-Passw0rd";
    private const string SecretPassword = "S3cret-Host-Password!";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _source = null!;
    private NKeeperApiFactory _target = null!;
    private HttpClient _sourceAdmin = null!;
    private HttpClient _targetAdmin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _source = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _target = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), adminPassword: TargetAdminPassword);
        _sourceAdmin = await _source.CreateSignedInClientAsync(Ct);
        _targetAdmin = _target.CreateClient();
        (await _targetAdmin.LoginAsync(NKeeperApiFactory.AdminUserName, TargetAdminPassword, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    [Fact]
    public async Task WholeInstance_MovesToAFreshOne_WithSecretsReEncryptedThere()
    {
        var (node, vm) = await SeedSourceAsync();
        // A domain account: no local password, signs in against Active Directory.
        await using (var seed = _source.Services.CreateAsyncScope())
        {
            var users = seed.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<NKeeper.Infrastructure.Identity.ApplicationUser>>();
            Assert.True((await users.CreateAsync(new NKeeper.Infrastructure.Identity.ApplicationUser { UserName = "carol", Email = "carol@corp.example.com", IsDirectoryAccount = true })).Succeeded);
        }

        var bundle = await ExportAsync();

        // A fresh instance can import; the preview says what's in the file without changing anything.
        Assert.True((await StatusAsync(_targetAdmin)).GetProperty("canImport").GetBoolean());
        var preview = await PostImportAsync(_targetAdmin, "import/preview", bundle, Passphrase);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var summary = await preview.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(summary.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Active Directory", StringComparison.Ordinal));
        Assert.DoesNotContain(summary.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("no password", StringComparison.Ordinal));
        Assert.Equal((3, 2, 1, 1, false), (summary.GetProperty("users").GetInt32(), summary.GetProperty("hosts").GetInt32(), summary.GetProperty("grants").GetInt32(), summary.GetProperty("credentials").GetInt32(), summary.GetProperty("imported").GetBoolean()));
        Assert.Equal(0, await CountHostsAsync(_target));

        var imported = await PostImportAsync(_targetAdmin, "import", bundle, Passphrase, confirm: true);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        // The importing administrator was replaced by the old server's accounts, so their session is over.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _targetAdmin.GetAsync("/api/hosts", Ct)).StatusCode);
        using var admin = _target.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.LoginAsync(NKeeperApiFactory.AdminUserName, TargetAdminPassword, Ct)).StatusCode);
        (await admin.LoginAsync(NKeeperApiFactory.AdminUserName, NKeeperApiFactory.AdminPassword, Ct)).EnsureSuccessStatusCode();
        using var bob = _target.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        var hosts = (await admin.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!;
        var movedVm = hosts.Single(h => h.Id == vm.Id);
        Assert.Equal((node.Id, 4, 8192, "libvirt:1"), (movedVm.HypervisorId!.Value, movedVm.CpuCores!.Value, movedVm.MemoryMiB!.Value, movedVm.VirtualMachineId!));
        var movedNode = hosts.Single(h => h.Id == node.Id);
        Assert.Equal(("ABC1234", "env", "production"), (movedNode.SerialNumber!, movedNode.Tags.Single().Name, movedNode.Tags.Single().Value!));
        Assert.Equal(("SHA256-OF-KEY", ServiceProtocol.Ssh), (movedNode.TrustedKeys.Single().Fingerprint, movedNode.TrustedKeys.Single().Protocol));
        // Bob's grant came along: he sees exactly the host he was given.
        Assert.Equal([vm.Id], (await bob.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!.Select(h => h.Id));

        // The secret decrypts on the new server with the new server's keys.
        await using var scope = _target.Services.CreateAsyncScope();
        var target = await scope.ServiceProvider.GetRequiredService<SessionTargetResolver>().ResolveAsync(
            new SshStartMessage { HostId = node.Id, ServiceIndex = 0, CredentialId = movedNode.Credentials.Single().Id },
            new RequestUser(Guid.NewGuid(), IsAdministrator: true),
            ServiceProtocol.Ssh,
            22,
            Ct);
        Assert.Equal(SecretPassword, target.Password);

        // The domain account is still one, so Active Directory sign-in can take it over.
        var carol = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Users.SingleAsync(u => u.UserName == "carol", Ct);
        Assert.True(carol.IsDirectoryAccount);
        Assert.Null(carol.PasswordHash);

        // The old key ring stayed behind: the new instance has only its own keys.
        var sourceKeys = await KeyIdsAsync(_source);
        Assert.Empty((await KeyIdsAsync(_target)).Intersect(sourceKeys));
    }

    [Fact]
    public async Task TheFile_HidesEverything_AndRefusesAWrongPassphraseOrAnyChange()
    {
        await SeedSourceAsync();
        var bundle = await ExportAsync();

        foreach (var secret in new[] { SecretPassword, "bob", "node1", "ABC1234" })
        {
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bundle), StringComparison.Ordinal);
        }

        await AssertRefusedAsync(bundle, "a different passphrase entirely", "passphrase is wrong");

        var flipped = (byte[])bundle.Clone();
        flipped[^20] ^= 0x01;
        // A one-chunk file can't tell a wrong passphrase from a change; either way nothing is imported.
        await AssertRefusedAsync(flipped, Passphrase, "was changed");

        var header = BundleFormat.ReadHeader(bundle);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bundle).Replace(header.ExportedBy, "mallory", StringComparison.Ordinal));
        await AssertRefusedAsync(tampered, Passphrase, null);

        await AssertRefusedAsync(bundle[..(bundle.Length - 5)], Passphrase, null);
        await AssertRefusedAsync("not a bundle"u8.ToArray(), Passphrase, "isn't an NKeeper export");
        Assert.Equal(0, await CountHostsAsync(_target));
    }

    [Fact]
    public async Task OnlyAFreshInstance_AcceptsAnImport()
    {
        await SeedSourceAsync();
        var bundle = await ExportAsync();

        // The source has hosts: importing there would overwrite them.
        var response = await PostImportAsync(_sourceAdmin, "import", bundle, Passphrase, confirm: true);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await StatusAsync(_sourceAdmin)).GetProperty("canImport").GetBoolean());

        // Importing needs the explicit confirmation.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostImportAsync(_targetAdmin, "import", bundle, Passphrase, confirm: false)).StatusCode);
    }

    [Fact]
    public async Task ABundleFromANewerNKeeper_IsRefused()
    {
        await SeedSourceAsync();
        await using var scope = _source.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_FromTheFuture', '10.0.0')""", Ct);

        var bundle = await ExportAsync();

        var response = await PostImportAsync(_targetAdmin, "import/preview", bundle, Passphrase);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("newer NKeeper", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_NeedsAnAdministrator_TheirPassword_AndAStrongPassphrase()
    {
        await SeedSourceAsync();
        using var bob = _source.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendJsonAsync(HttpMethod.Post, "/api/admin/transfer/export", new { password = "Bob-Passw0rd-123", passphrase = Passphrase }, Ct)).StatusCode);
        var wrongPassword = await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/admin/transfer/export", new { password = "not-my-password", passphrase = Passphrase }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPassword.StatusCode);
        Assert.Contains("password_incorrect", await wrongPassword.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var weak = await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/admin/transfer/export", new { password = NKeeperApiFactory.AdminPassword, passphrase = "short" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task LockingAfterExport_StopsChanges_UntilUnlocked()
    {
        var (node, _) = await SeedSourceAsync();
        await ExportAsync(lockAfterExport: true);

        var status = await StatusAsync(_sourceAdmin);
        Assert.Equal((true, NKeeperApiFactory.AdminUserName), (status.GetProperty("locked").GetBoolean(), status.GetProperty("lockedBy").GetString()));
        var edit = await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "late", ipAddress = "10.0.0.99" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains("instance_locked", await edit.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await _sourceAdmin.SendJsonAsync(HttpMethod.Delete, $"/api/hosts/{node.Id}", null, Ct)).StatusCode);
        // Reading still works.
        Assert.Equal(HttpStatusCode.OK, (await _sourceAdmin.GetAsync("/api/hosts", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/admin/transfer/unlock", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name = "late", ipAddress = "10.0.0.99" }, Ct)).StatusCode);
    }

    /// <summary>A KVM node with a serial, a tag, a credential and a trusted key; a VM on it; and bob, who may use the VM.</summary>
    private async Task<(HostResponse Node, HostResponse Vm)> SeedSourceAsync()
    {
        var node = await CreateAsync(_sourceAdmin, new
        {
            name = "node1",
            ipAddress = "10.0.0.1",
            serialNumber = "ABC1234",
            cpuCores = 32,
            memoryMiB = 131072,
            tags = new[] { new { name = "env", value = "production" } },
            services = new[] { new { protocol = "Ssh" } },
            credentials = new[] { new { username = "root", password = SecretPassword } },
        });
        var vm = await CreateAsync(_sourceAdmin, new
        {
            name = "web01",
            ipAddress = "10.0.0.50",
            isVirtualMachine = true,
            hypervisorId = node.Id,
        });
        await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/hosts/hypervisor-links", new
        {
            links = new[] { new { guestId = vm.Id, hypervisorId = node.Id, virtualMachineId = "libvirt:1", cpuCores = 4, memoryMiB = 8192 } },
        }, Ct);

        await using (var scope = _source.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            db.HostTrustedKeys.Add(new HostTrustedKey { HostId = node.Id, Protocol = ServiceProtocol.Ssh, Port = 22, Algorithm = "ssh-ed25519", Fingerprint = "SHA256-OF-KEY", TrustedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        await _sourceAdmin.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        var users = await _sourceAdmin.GetFromJsonAsync<List<JsonElement>>("/api/users", Ct);
        var bobId = users!.Single(u => u.GetProperty("userName").GetString() == "bob").GetProperty("id").GetString();
        (await _sourceAdmin.SendJsonAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new { grants = new[] { new { hostId = vm.Id, level = "Use" } } }, Ct)).EnsureSuccessStatusCode();
        return (node, vm);
    }

    private async Task<byte[]> ExportAsync(bool lockAfterExport = false)
    {
        var response = await _sourceAdmin.SendJsonAsync(
            HttpMethod.Post, "/api/admin/transfer/export", new { password = NKeeperApiFactory.AdminPassword, passphrase = Passphrase, lockAfterExport }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task<HttpResponseMessage> PostImportAsync(HttpClient client, string path, byte[] bundle, string passphrase, bool confirm = false)
    {
        var token = await client.GetAntiforgeryTokenAsync(Ct);
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bundle) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "nkeeper.nkeeper" },
            { new StringContent(passphrase), "passphrase" },
            { new StringContent(confirm ? "true" : "false"), "confirm" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/transfer/{path}") { Content = content };
        request.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);
        return await client.SendAsync(request, Ct);
    }

    private async Task AssertRefusedAsync(byte[] bundle, string passphrase, string? message)
    {
        var response = await PostImportAsync(_targetAdmin, "import", bundle, passphrase, confirm: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("bundle_invalid", body, StringComparison.Ordinal);
        if (message is not null)
        {
            Assert.Contains(message, body, StringComparison.Ordinal);
        }
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/admin/transfer/status", Ct);

    private static async Task<HostResponse> CreateAsync(HttpClient client, object host)
    {
        var response = await client.SendJsonAsync(HttpMethod.Post, "/api/hosts", host, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private static async Task<int> CountHostsAsync(NKeeperApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Hosts.CountAsync(Ct);
    }

    private static async Task<List<string?>> KeyIdsAsync(NKeeperApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().DataProtectionKeys.Select(k => k.FriendlyName).ToListAsync(Ct);
    }
}
