using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Controllers;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>Files over SFTP against a real OpenSSH server.</summary>
public sealed class SftpTests(PostgresServerFixture postgres, SshServerFixture ssh) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeTempSpace _tempSpace = new();
    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HostResponse _host = null!;
    private string _folder = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        ssh.SkipIfUnavailable();
        _factory = new NKeeperApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            configureServices: services => services.AddSingleton<SftpTempSpace>(_tempSpace));
        _admin = await _factory.CreateSignedInClientAsync(Ct);
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "files01",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { protocol = "Ssh", port = ssh.Port } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        }, Ct);
        _host = (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;

        // Trust the key, then work in a folder of this test's own (tests share the server).
        var first = await ListAsync(null);
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var home = await ListAsync(null, first.GetProperty("hostKey").GetProperty("fingerprint").GetString());
        _folder = $"{home.GetProperty("path").GetString()}/nk-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("mkdir", new { path = _folder })).StatusCode);
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
    public async Task FilesCanBeUploaded_Listed_Downloaded_Renamed_AndDeleted()
    {
        var upload = await UploadAsync("notes.txt", "hello from NKeeper");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var listing = await ListAsync(_folder);
        var entry = Assert.Single(listing.GetProperty("entries").EnumerateArray());
        Assert.Equal(("notes.txt", "file", 18L), (entry.GetProperty("name").GetString(), entry.GetProperty("kind").GetString(), entry.GetProperty("size").GetInt64()));

        Assert.Equal("hello from NKeeper", await DownloadAsync($"{_folder}/notes.txt"));

        // An upload over an existing file needs Overwrite.
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync("notes.txt", "again")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync("notes.txt", "replaced", overwrite: true)).StatusCode);
        Assert.Equal("replaced", await DownloadAsync($"{_folder}/notes.txt"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("rename", new { path = $"{_folder}/notes.txt", newPath = "renamed.txt" })).StatusCode);
        Assert.Equal(["renamed.txt"], (await ListAsync(_folder)).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        // A folder with something in it isn't deleted; an empty one and a file are.
        var notEmpty = await PostAsync("delete", new { path = _folder });
        Assert.Contains("isn't empty", await notEmpty.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("delete", new { path = $"{_folder}/renamed.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("delete", new { path = _folder })).StatusCode);

        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=files&limit=50", Ct);
        Assert.Equal(
            ["sftp.delete", "sftp.delete", "sftp.rename", "sftp.download", "sftp.upload", "sftp.download", "sftp.upload", "sftp.mkdir"],
            audit.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task UploadedNames_CantClimbOutOfTheFolder()
    {
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync("../../escape.txt", "x")).StatusCode);

        Assert.Equal(["escape.txt"], (await ListAsync(_folder)).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task DownloadLinks_WorkOnce_ForTheUserWhoAsked()
    {
        await UploadAsync("secret.txt", "for admin only");
        var link = await LinkAsync($"{_folder}/secret.txt");

        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(link, Ct)).StatusCode);

        // Bob's attempt used it up.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(link, Ct)).StatusCode);
        var fresh = await LinkAsync($"{_folder}/secret.txt");
        Assert.Equal("for admin only", await _admin.GetStringAsync(fresh, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(fresh, Ct)).StatusCode);
    }

    [Fact]
    public async Task FolderSize_AddsUpTheFilesBelowIt()
    {
        await UploadAsync("top.txt", "12345");
        var inner = $"{_folder}/inner";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("mkdir", new { path = inner })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync("nested.txt", "1234567", directory: inner)).StatusCode);

        var size = await PostAsync("folder-size", new { path = _folder });
        Assert.True(size.IsSuccessStatusCode, await size.Content.ReadAsStringAsync(Ct));
        var body = await size.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((12L, true), (body.GetProperty("size").GetInt64(), body.GetProperty("complete").GetBoolean()));

        // A folder that isn't there is an error, not an empty folder.
        Assert.False((await PostAsync("folder-size", new { path = $"{_folder}/missing" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Folders_DownloadAsAZip_MadeInTemp_AuditedByTheirRealPath_ThenRemoved()
    {
        await UploadAsync("top.txt", "12345");
        var inner = $"{_folder}/inner";
        var empty = $"{_folder}/empty";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("mkdir", new { path = inner })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("mkdir", new { path = empty })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync("nested.txt", "1234567", directory: inner)).StatusCode);

        using var response = await _admin.GetAsync(await LinkAsync(_folder), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{Path.GetFileName(_folder)}.zip", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal(["empty/", "inner/nested.txt", "top.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var nested = new StreamReader(zip.GetEntry("inner/nested.txt")!.Open());
        Assert.Equal("1234567", await nested.ReadToEndAsync(Ct));

        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=files&limit=50", Ct);
        var download = audit.GetProperty("events").EnumerateArray().First(e => e.GetProperty("action").GetString() == "sftp.download");
        Assert.Equal($"{_folder} (folder, 12 bytes)", download.GetProperty("detail").GetString());

        // The zip goes once the response has ended.
        for (var attempt = 0; attempt < 50 && Directory.EnumerateFiles(Path.GetTempPath(), "nkeeper-sftp-*.zip").Any(); attempt++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), "nkeeper-sftp-*.zip"));
    }

    [Fact]
    public async Task Folders_TooBigForTheServersFreeSpace_AreRefusedBeforeZipping()
    {
        await UploadAsync("top.txt", "12345");
        _tempSpace.Bytes = 50L * 1024 * 1024;

        var refused = await PostAsync("download-link", new { path = _folder });
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("too big to zip", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A single file isn't zipped, so it doesn't depend on the space.
        Assert.Equal("12345", await DownloadAsync($"{_folder}/top.txt"));

        _tempSpace.Bytes = 200L * 1024 * 1024;
        Assert.True((await _admin.GetAsync(await LinkAsync(_folder), Ct)).IsSuccessStatusCode);
    }

    private sealed class FakeTempSpace : SftpTempSpace
    {
        public long Bytes { get; set; } = long.MaxValue;

        public override long AvailableBytes => Bytes;
    }

    private async Task<JsonElement> ListAsync(string? path, string? trust = null)
    {
        var response = await PostAsync("list", new { path, trustFingerprint = trust });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<HttpResponseMessage> PostAsync(string action, object fields)
    {
        var body = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body["hostId"] = _host.Id;
        body["serviceIndex"] = 0;
        body["credentialId"] = _host.Credentials[0].Id;
        return _admin.SendJsonAsync(HttpMethod.Post, $"/api/sftp/{action}", body, Ct);
    }

    private async Task<string> LinkAsync(string path)
    {
        var response = await PostAsync("download-link", new { path });
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("url").GetString()!;
    }

    private async Task<string> DownloadAsync(string path) => await _admin.GetStringAsync(await LinkAsync(path), Ct);

    private async Task<HttpResponseMessage> UploadAsync(string name, string content, bool overwrite = false, string? directory = null)
    {
        var token = await _admin.GetAntiforgeryTokenAsync(Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(_host.Id.ToString()), "hostId" },
            { new StringContent("0"), "serviceIndex" },
            { new StringContent(_host.Credentials[0].Id.ToString()), "credentialId" },
            { new StringContent(directory ?? _folder), "directory" },
            { new StringContent(overwrite ? "true" : "false"), "overwrite" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "files", name },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sftp/upload") { Content = form };
        request.Headers.Add(AuthClientExtensions.AntiforgeryHeader, token);
        return await _admin.SendAsync(request, Ct);
    }
}
