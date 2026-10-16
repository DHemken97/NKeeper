using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Inventory;
using NKeeper.Api.Inventory.Import;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Importing mRemoteNG connection files: reading and decrypting them, then adding, updating and deleting hosts.</summary>
public sealed class HostImportTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RealExport => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Import", "mremoteng-export.xml"));

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _client = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void RealExport_IsReadAndDecrypted_WithTheDefaultMasterPassword()
    {
        var connections = MRemoteNgFile.Read(RealExport, masterPassword: null);

        Assert.Collection(
            connections,
            ssh =>
            {
                Assert.Equal(("TestHost-SSH", "ip", "TestFolder"), (ssh.Name, ssh.Hostname, ssh.FolderPath));
                Assert.Equal((ServiceProtocol.Ssh, (int?)22), (ssh.Protocol, ssh.Port));
                Assert.Equal(("username", "password"), (ssh.Username, ssh.Password));
            },
            rdp =>
            {
                Assert.Equal(("TEST-Host-RDP", "ip2", ServiceProtocol.Rdp, (int?)3389), (rdp.Name, rdp.Hostname, rdp.Protocol, rdp.Port));
                Assert.Equal(("usr", "pass"), (rdp.Username, rdp.Password));
            });
    }

    [Fact]
    public void MasterPassword_IsRequired_AndChecked()
    {
        var file = MRemoteFile("s3cret-master", Connection("db01", "10.0.0.20", password: "db-pass"));

        var missing = Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read(file, null));
        Assert.Equal(("masterPassword", true), (missing.Field, missing.Message.Contains("protected with a master password", StringComparison.Ordinal)));
        var wrong = Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read(file, "not-it"));
        Assert.Contains("doesn't open this file", wrong.Message, StringComparison.Ordinal);

        Assert.Equal("db-pass", Assert.Single(MRemoteNgFile.Read(file, "s3cret-master")).Password);
        // A password typed for a file without one still opens it.
        Assert.Single(MRemoteNgFile.Read(MRemoteFile(MRemoteNgFile.DefaultMasterPassword, Connection("a", "10.0.0.1")), "anything"));
    }

    [Fact]
    public void InheritedValues_ComeFromTheNearestFolder()
    {
        var file = MRemoteFile(
            MRemoteNgFile.DefaultMasterPassword,
            Folder(
                "Outer",
                """Username="ops" Domain="CORP" Port="2222" Protocol="SSH2" """,
                Folder(
                    "Inner",
                    """InheritUsername="true" InheritDomain="true" InheritPort="true" InheritProtocol="true" Username="ignored" Port="9" """,
                    Connection("app01", "app01.corp.example", extra: """InheritUsername="true" InheritDomain="true" InheritPort="true" InheritProtocol="true" """))));

        var app = Assert.Single(MRemoteNgFile.Read(file, null));

        Assert.Equal(("ops", "CORP", ServiceProtocol.Ssh, (int?)2222, "Outer/Inner"), (app.Username, app.Domain, app.Protocol, app.Port, app.FolderPath));
    }

    [Fact]
    public void UnreadableFiles_AreExplained()
    {
        Assert.Contains("isn't an mRemoteNG", Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read("<hosts/>", null)).Message, StringComparison.Ordinal);
        Assert.Contains("isn't valid XML", Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read("<oops", null)).Message, StringComparison.Ordinal);
        Assert.Contains("older mRemoteNG", Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read(MRemoteFile(MRemoteNgFile.DefaultMasterPassword, "", version: "2.5"), null)).Message, StringComparison.Ordinal);
        // Entity tricks are refused rather than expanded.
        Assert.Throws<InventoryValidationException>(() => MRemoteNgFile.Read("""<!DOCTYPE x [<!ENTITY e "boom">]><Connections>&e;</Connections>""", null));
    }

    [Fact]
    public async Task DryRun_ReportsTheChanges_WithoutSavingThem()
    {
        var result = await ImportAsync(RealExport, add: true, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(["TestHost-SSH", "TEST-Host-RDP"], result.Added);
        Assert.Empty(await HostsAsync());
    }

    [Fact]
    public async Task Add_CreatesHostsWithServicesCredentialsAndFolderTags()
    {
        var result = await ImportAsync(RealExport, add: true);

        Assert.Equal(["TestHost-SSH", "TEST-Host-RDP"], result.Added);
        var ssh = Assert.Single(await HostsAsync(), h => h.Name == "TestHost-SSH");
        Assert.Equal(("ip", (string?)null), (ssh.Hostname, ssh.IpAddress));
        // mRemoteNG writes every port; the protocol's default is stored as "default".
        Assert.Equal(new HostServiceResponse(ServiceProtocol.Ssh, null, null, null), Assert.Single(ssh.Services));
        Assert.Equal(new HostTagResponse("folder", "TestFolder"), Assert.Single(ssh.Tags));
        var credential = Assert.Single(ssh.Credentials);
        Assert.Equal(("username", true), (credential.Username, credential.HasPassword));
        Assert.Equal("password", await StoredPasswordAsync(credential.Id));
    }

    [Fact]
    public async Task Update_AppliesTheFile_AndKeepsWhatItDoesNotMention()
    {
        var existing = await CreateHostAsync("TestHost-SSH", "10.9.9.9", new { protocol = "Http", port = 8080 });
        await ImportAsync(RealExport, add: true, update: false);

        var notUpdated = Assert.Single(await HostsAsync(), h => h.Id == existing.Id);
        Assert.Equal("10.9.9.9", notUpdated.IpAddress);

        var result = await ImportAsync(RealExport, update: true);

        Assert.Equal(["TestHost-SSH"], result.Updated);
        Assert.Equal(["TEST-Host-RDP"], result.Unchanged);
        var updated = Assert.Single(await HostsAsync(), h => h.Id == existing.Id);
        Assert.Equal(("ip", (string?)null), (updated.Hostname, updated.IpAddress));
        Assert.Equal([ServiceProtocol.Http, ServiceProtocol.Ssh], updated.Services.Select(s => s.Protocol));
        Assert.Equal("username", Assert.Single(updated.Credentials).Username);

        // Importing the same file again changes nothing.
        var again = await ImportAsync(RealExport, update: true);
        Assert.Empty(again.Updated);
        Assert.Equal(2, again.Unchanged.Count);
    }

    [Fact]
    public async Task Delete_RemovesOnlyHostsMissingFromTheFile()
    {
        await CreateHostAsync("TestHost-SSH", "10.0.0.1");
        var stale = await CreateHostAsync("decommissioned", "10.0.0.2");

        var preview = await ImportAsync(RealExport, delete: true, dryRun: true);
        Assert.Equal(["decommissioned"], preview.Deleted);
        Assert.Equal(2, (await HostsAsync()).Count);

        var result = await ImportAsync(RealExport, delete: true);

        Assert.Equal(["decommissioned"], result.Deleted);
        Assert.Equal(["TestHost-SSH"], (await HostsAsync()).Select(h => h.Name));
        Assert.Contains(result.Skipped, s => s.Name == "TEST-Host-RDP" && s.Reason.Contains("Add is off", StringComparison.Ordinal));
        Assert.DoesNotContain(await HostsAsync(), h => h.Id == stale.Id);
    }

    [Fact]
    public async Task SameNameConnections_MergeIntoOneHost_AndBadOnesAreSkipped()
    {
        var file = MRemoteFile(
            MRemoteNgFile.DefaultMasterPassword,
            Connection("web01", "10.0.0.30", protocol: "SSH2", port: "22", username: "root")
                + Connection("WEB01", "10.0.0.30", protocol: "HTTPS", port: "8443")
                + Connection("web01", "10.0.0.99", protocol: "RDP", port: "3389")
                + Connection("nowhere", "")
                + Connection("weird", "not a host!", protocol: "ICA", port: "1494"));

        var result = await ImportAsync(file, add: true);

        Assert.Equal(["web01"], result.Added);
        Assert.Equal(["web01", "nowhere", "weird"], result.Skipped.Select(s => s.Name));
        var web = Assert.Single(await HostsAsync());
        Assert.Equal([new HostServiceResponse(ServiceProtocol.Ssh, null, null, null), new HostServiceResponse(ServiceProtocol.Https, 8443, null, null)], web.Services);
    }

    [Fact]
    public async Task Import_IsForAdministrators_AndNeedsAnAction()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/import", new { content = RealExport }, Ct)).StatusCode);

        await _client.SendJsonAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", role = "User", password = "Bob-Passw0rd-123", mustChangePassword = false }, Ct);
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendJsonAsync(HttpMethod.Post, "/api/hosts/import", new { content = RealExport, add = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task WrongMasterPassword_IsAFieldError()
    {
        var response = await _client.SendJsonAsync(
            HttpMethod.Post, "/api/hosts/import", new { content = MRemoteFile("right", Connection("a", "10.0.0.1")), masterPassword = "wrong", add = true }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("masterPassword", out _), errors.ToString());
    }

    private async Task<HostImportResponse> ImportAsync(string content, bool add = false, bool update = false, bool delete = false, bool dryRun = false)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts/import", new { content, add, update, delete, dryRun }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostImportResponse>(Json, Ct))!;
    }

    private async Task<List<HostResponse>> HostsAsync() => (await _client.GetFromJsonAsync<List<HostResponse>>("/api/hosts", Json, Ct))!;

    private async Task<HostResponse> CreateHostAsync(string name, string ip, params object[] services)
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = ip, services }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<string> StoredPasswordAsync(Guid credentialId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
        var stored = await db.Set<HostCredential>().SingleAsync(c => c.Id == credentialId, Ct);
        return scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(stored.ProtectedPassword!);
    }

    /// <summary>An mRemoteNG 2.6 file, encrypted the way mRemoteNG does with the given master password.</summary>
    private static string MRemoteFile(string masterPassword, string nodes, string version = "2.6")
    {
        const int iterations = 1000;
        var marker = MRemoteNgFile.Encrypt(masterPassword == MRemoteNgFile.DefaultMasterPassword ? "ThisIsNotProtected" : "ThisIsProtected", masterPassword, iterations);
        var encryptedNodes = nodes;
        foreach (var plain in PlainPasswords(nodes))
        {
            encryptedNodes = encryptedNodes.Replace($"Password=\"{plain}\"", $"Password=\"{MRemoteNgFile.Encrypt(plain, masterPassword, iterations)}\"", StringComparison.Ordinal);
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{iterations}" FullFileEncryption="false" Protected="{marker}" ConfVersion="{version}">
            {encryptedNodes}
            </mrng:Connections>
            """;
    }

    private static IEnumerable<string> PlainPasswords(string nodes) =>
        System.Text.RegularExpressions.Regex.Matches(nodes, "Password=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct();

    private static string Folder(string name, string attributes, string children) =>
        $"""<Node Name="{SecurityElement.Escape(name)}" Type="Container" Id="{Guid.NewGuid()}" {attributes}>{children}</Node>""";

    private static string Connection(
        string name, string hostname, string protocol = "RDP", string port = "3389", string? username = null, string? password = null, string extra = "") =>
        $"""<Node Name="{SecurityElement.Escape(name)}" Type="Connection" Id="{Guid.NewGuid()}" Hostname="{SecurityElement.Escape(hostname)}" Protocol="{protocol}" Port="{port}" Username="{username}" Domain="" Password="{password}" Descr="" {extra}/>""";
}
