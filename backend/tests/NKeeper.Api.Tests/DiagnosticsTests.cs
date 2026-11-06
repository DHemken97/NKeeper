using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;
using NKeeper.Api.Diagnostics;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>What the redaction takes out of free text; no server needed.</summary>
public sealed class DiagnosticsRedactionTests
{
    private static readonly DiagnosticsRedactor Redactor = new(new byte[32]);

    [Theory]
    [InlineData("login failed for root@10.0.0.5:22", "root@10.0.0.5")]
    [InlineData("Password=hunter2;Host=db", "hunter2")]
    [InlineData("pwd: 'correct horse'", "correct horse")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "abcdefgh12345678")]
    [InlineData("connect ldaps://svc:s3cret@dc1.corp.example.com/dc=x?y", "s3cret")]
    [InlineData("connect ldaps://svc:s3cret@dc1.corp.example.com/dc=x?y", "dc1.corp.example.com")]
    [InlineData("reached 192.168.1.20 then fe80::1ff:fe23:4567:890a", "192.168.1.20")]
    [InlineData("reached 192.168.1.20 then fe80::1ff:fe23:4567:890a", "fe80::1ff")]
    [InlineData("mac aa:bb:cc:dd:ee:ff seen", "aa:bb:cc:dd:ee:ff")]
    [InlineData("mail alice@example.com", "alice@example.com")]
    [InlineData(@"signed in as CORP\alice", "alice")]
    [InlineData(@"can't open C:\Users\alice\secret.txt", "alice")]
    [InlineData("can't open /home/alice/secret.txt", "alice")]
    [InlineData("resolving db01.prod.example.com failed", "db01.prod.example.com")]
    [InlineData("resolving intranet.local failed", "intranet.local")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "MIIabc")]
    [InlineData("key 0123456789abcdef0123456789abcdef0123", "0123456789abcdef")]
    public void Sensitive_text_is_taken_out(string text, string sensitive) =>
        Assert.DoesNotContain(sensitive, Redactor.Redact(text), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Known_names_become_tokens_only_as_whole_names()
    {
        var redactor = new DiagnosticsRedactor(new byte[32]);
        redactor.SetKnownNames(["erp-prod", "bob", "x"]);

        var text = redactor.Redact("erp-prod refused bob, not erp-production or bobcat, x");

        Assert.DoesNotContain("erp-prod refused", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" bob,", text, StringComparison.Ordinal);
        Assert.Contains("erp-production", text, StringComparison.Ordinal);
        Assert.Contains("bobcat", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_stable_and_depend_on_the_secret()
    {
        var first = Redactor.Redact("host 10.0.0.5");
        Assert.Equal(first, Redactor.Redact("host 10.0.0.5"));
        Assert.NotEqual(first, Redactor.Redact("host 10.0.0.6"));
        Assert.NotEqual(first, new DiagnosticsRedactor(Enumerable.Repeat((byte)7, 32).ToArray()).Redact("host 10.0.0.5"));
    }

    [Fact]
    public void Code_names_and_script_paths_stay_readable()
    {
        Assert.Contains("System.IO.IOException", Redactor.Redact("System.IO.IOException: failed"), StringComparison.Ordinal);
        var stack = Redactor.Redact("at load (https://nkeeper.corp.example.com/assets/index-abc.js?v=secretvalue:1:2)", keepPaths: true);
        Assert.Contains("/assets/index-abc.js", stack, StringComparison.Ordinal);
        Assert.DoesNotContain("corp.example.com", stack, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", stack, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0", "1.0")]
    [InlineData("1.0.0+9b20fce", "1.0")]
    [InlineData("1.0.1+9b20fce", "1.0.1")]
    [InlineData("1.2.0-rc.1", "1.2.0-rc.1")]
    public void The_version_is_shortened_for_people(string full, string display) => Assert.Equal(display, AppVersion.Shorten(full));
}

/// <summary>The file itself: rotation, the size limit, low disk space. No server needed.</summary>
public sealed class DiagnosticsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nkeeper-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDisk _disk = new();
    private readonly FixedClock _clock = new();

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeDisk : DiagnosticsDisk
    {
        public long Free { get; set; } = long.MaxValue;

        public override long AvailableBytes(string directory) => Free;
    }

    private DiagnosticsLog NewLog() => new(Options.Create(new DiagnosticsSettings { Directory = _directory }), _disk, _clock);

    private string Current => Path.Combine(_directory, DiagnosticsLog.CurrentFileName);

    private string Older => Path.Combine(_directory, DiagnosticsLog.OlderFileName);

    private static List<JsonElement> Lines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Lines_are_json_with_their_time_and_type()
    {
        using var log = NewLog();
        log.Write("test", new { value = 5 });

        var line = Assert.Single(Lines(Current));
        Assert.Equal("test", line.GetProperty("type").GetString());
        Assert.Equal(5, line.GetProperty("value").GetInt32());
        Assert.Equal(_clock.GetUtcNow(), line.GetProperty("t").GetDateTimeOffset());
    }

    [Fact]
    public void The_total_never_passes_the_limit_and_the_oldest_data_goes_first()
    {
        using var log = NewLog();
        log.Update(enabled: true, DiagnosticsLog.MinMegabytes);
        var filler = new string('x', 100_000);
        for (var i = 0; i < 500; i++)
        {
            log.Write("test", new { i, filler });
        }

        var status = log.Status();
        Assert.True(status.SizeBytes <= DiagnosticsLog.MinMegabytes * 1024L * 1024L, $"{status.SizeBytes} bytes");
        Assert.NotNull(status.DroppedUtc);
        Assert.True(File.Exists(Older));
        Assert.Equal(499, Lines(Current)[^1].GetProperty("i").GetInt32());
        Assert.True(Lines(Older)[0].GetProperty("i").GetInt32() > 0);
    }

    [Fact]
    public void Writing_pauses_when_disk_space_is_low_and_says_how_long_for()
    {
        using var log = NewLog();
        log.SetReserve(1000);
        _disk.Free = 500;

        log.Write("lost", new { });
        Assert.False(File.Exists(Current));
        Assert.True(log.Status().PausedForDiskSpace);

        _disk.Free = 2000;
        _clock.Advance(TimeSpan.FromMinutes(2));
        log.Write("kept", new { });

        var lines = Lines(Current);
        Assert.Equal(["gap", "kept"], lines.Select(l => l.GetProperty("type").GetString()));
        Assert.Equal("low_disk_space", lines[0].GetProperty("reason").GetString());
        Assert.False(log.Status().PausedForDiskSpace);
    }

    [Fact]
    public void The_same_error_is_written_once_with_a_count()
    {
        using var log = NewLog();
        for (var i = 0; i < 1000; i++)
        {
            log.RecordError("area", "Error", "It broke", "System.Exception: x", "at A\nat B");
        }

        log.RecordError("area", "Error", "Something else", null, null);
        log.FlushRepeats();

        var lines = Lines(Current);
        Assert.Equal(["error", "error", "repeated"], lines.Select(l => l.GetProperty("type").GetString()));
        Assert.Equal(999, lines[2].GetProperty("count").GetInt32());
        Assert.Equal(lines[0].GetProperty("fingerprint").GetString(), lines[2].GetProperty("fingerprint").GetString());

        log.FlushRepeats(forget: true);
        log.RecordError("area", "Error", "It broke", "System.Exception: x", "at A\nat B");
        Assert.Equal("error", Lines(Current)[^1].GetProperty("type").GetString());
    }

    [Fact]
    public void Turned_off_it_writes_nothing_and_stays_off_after_a_restart()
    {
        using (var log = NewLog())
        {
            log.Update(enabled: false, 300);
            log.Write("lost", new { });
            log.RecordError("area", "Error", "lost", null, null);
            Assert.False(File.Exists(Current));
        }

        using var again = NewLog();
        Assert.False(again.Enabled);
        Assert.Equal(300, again.Status().MaxMegabytes);
    }

    [Fact]
    public void Tokens_survive_a_restart()
    {
        string token;
        using (var log = NewLog())
        {
            token = log.Redactor.Token("host", "erp");
        }

        using var again = NewLog();
        Assert.Equal(token, again.Redactor.Token("host", "erp"));
    }

    [Fact]
    public void A_directory_it_cannot_use_breaks_nothing_and_says_why()
    {
        Directory.CreateDirectory(_directory);
        var aFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(aFile, "");
        using var log = new DiagnosticsLog(Options.Create(new DiagnosticsSettings { Directory = aFile }), _disk, _clock);

        log.Write("lost", new { });

        Assert.NotNull(log.Status().WriteError);
    }

    [Fact]
    public async Task The_download_has_a_manifest_and_every_line_but_never_the_secret()
    {
        using var log = NewLog();
        log.Write("first", new { });
        using var output = new MemoryStream();
        await log.WriteZipAsync(output, TestContext.Current.CancellationToken);

        output.Position = 0;
        await using var zip = new ZipArchive(output);
        Assert.Equal(["manifest.json", DiagnosticsLog.CurrentFileName], zip.Entries.Select(e => e.Name));
        Assert.DoesNotContain(zip.Entries, e => e.Name == "state.json");
        using var manifest = JsonDocument.Parse(await zip.GetEntry("manifest.json")!.OpenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AppVersion.Full, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(_clock.GetUtcNow(), log.Status().LastDownloadedUtc);
    }
}

/// <summary>Settings ▸ Diagnostics and what the running API puts in the file.</summary>
public sealed class DiagnosticsTests(PostgresServerFixture postgres) : IAsyncLifetime
{
    private sealed class RoomyDisk : DiagnosticsDisk
    {
        public override long AvailableBytes(string directory) => long.MaxValue;
    }

    private NKeeperApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new NKeeperApiFactory(await postgres.CreateDatabaseAsync(Ct), configureServices: services => services.AddSingleton<DiagnosticsDisk, RoomyDisk>());
        _admin = await _factory.CreateSignedInClientAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private DiagnosticsLog Log => _factory.Services.GetRequiredService<DiagnosticsLog>();

    private List<JsonElement> Lines()
    {
        Log.Status();
        using var file = new FileStream(Path.Combine(_factory.DiagnosticsDirectory, DiagnosticsLog.CurrentFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)];
    }

    [Fact]
    public async Task Administrators_see_the_status_and_version_and_change_the_settings()
    {
        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/diagnostics", Ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(200, status.GetProperty("maxMegabytes").GetInt32());
        Assert.Equal("1.0", status.GetProperty("version").GetString());

        var saved = await _admin.SendJsonAsync(HttpMethod.Put, "/api/admin/diagnostics", new { enabled = false, maxMegabytes = 500 }, Ct);
        saved.EnsureSuccessStatusCode();
        var after = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(after.GetProperty("enabled").GetBoolean());
        Assert.Equal(500, after.GetProperty("maxMegabytes").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Put, "/api/admin/diagnostics", new { enabled = true, maxMegabytes = 19 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.SendJsonAsync(HttpMethod.Put, "/api/admin/diagnostics", new { enabled = true, maxMegabytes = 10_241 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_read_change_or_download_it_but_anyone_signed_in_can_report_an_error()
    {
        const string password = "Plain-User-Passw0rd";
        await _factory.CreateUserAsync("plain", "plain@nkeeper.test", password);
        using var plain = _factory.CreateDefaultClient(new CookieContainerHandler());
        (await plain.LoginAsync("plain", password, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/diagnostics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/diagnostics/download", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.SendJsonAsync(HttpMethod.Put, "/api/admin/diagnostics", new { enabled = false, maxMegabytes = 200 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await plain.SendJsonAsync(HttpMethod.Post, "/api/diagnostics/browser-errors", new { message = "x" }, Ct)).StatusCode);

        using var anonymous = _factory.CreateDefaultClient(new CookieContainerHandler());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendJsonAsync(HttpMethod.Post, "/api/diagnostics/browser-errors", new { message = "x" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Downloading_is_audited_and_gives_a_zip()
    {
        var response = await _admin.GetAsync("/api/admin/diagnostics/download", Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        await using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct));
        Assert.Contains(zip.Entries, e => e.Name == "manifest.json");

        var audit = await _admin.GetFromJsonAsync<JsonElement>("/api/audit/events", Ct);
        Assert.Contains(audit.GetProperty("events").EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.diagnostics_download");
        Assert.True((await _admin.GetFromJsonAsync<JsonElement>("/api/admin/diagnostics", Ct)).TryGetProperty("lastDownloadedUtc", out var when) && when.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Browser_errors_are_redacted()
    {
        var host = new { name = "erp-prod", ipAddress = "10.0.0.9", services = Array.Empty<object>() };
        (await _admin.SendJsonAsync(HttpMethod.Post, "/api/hosts", host, Ct)).EnsureSuccessStatusCode();
        await Monitor().TickAsync(Ct);

        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/diagnostics/browser-errors", new
        {
            message = "Failed for erp-prod at 10.0.0.9 password=hunter2",
            stack = "at f (https://nkeeper.example.com/assets/app.js:1:2)",
            page = "/hosts/3f2504e0-4f89-11d3-9a0c-0305e82c3301?q=secret",
        }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var line = Lines().Last(l => l.GetProperty("type").GetString() == "error" && l.GetProperty("area").GetString() == "browser");
        var text = line.GetRawText();
        Assert.DoesNotContain("erp-prod", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nkeeper.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains("/assets/app.js", text, StringComparison.Ordinal);
        Assert.Equal("/hosts/:id", line.GetProperty("context").GetProperty("page").GetString());
    }

    [Fact]
    public async Task Oversized_browser_reports_are_refused()
    {
        var response = await _admin.SendJsonAsync(HttpMethod.Post, "/api/diagnostics/browser-errors", new { message = new string('x', 2001) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Logged_errors_keep_their_template_and_numbers_but_not_their_string_values()
    {
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NKeeper.Test");
        logger.LogError(new InvalidOperationException("Can't reach 10.0.0.9"), "Failed for {Host} after {Attempts} attempts", "erp-prod", 3);

        var line = Lines().Last(l => l.GetProperty("type").GetString() == "error" && l.GetProperty("area").GetString() == "NKeeper.Test");
        var text = line.GetRawText();
        Assert.Equal("Failed for {Host} after {Attempts} attempts", line.GetProperty("message").GetString());
        Assert.Equal("3", line.GetProperty("context").GetProperty("values").GetProperty("Attempts").GetString());
        Assert.DoesNotContain("erp-prod", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.9", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", line.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Information_messages_stay_out_of_the_file()
    {
        await Monitor().TickAsync(Ct);
        _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NKeeper.Test").LogInformation("Only a note");
        Assert.DoesNotContain(Lines(), l => l.GetRawText().Contains("Only a note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Usage_counts_route_patterns_and_users_never_the_values()
    {
        var id = Guid.NewGuid();
        await _admin.GetAsync($"/api/hosts/{id}", Ct);
        await _admin.GetAsync($"/api/hosts/{Guid.NewGuid()}", Ct);
        await _admin.GetAsync("/api/hosts?q=topsecret", Ct);

        Log.Usage.Flush();

        var usage = Lines().Where(l => l.GetProperty("type").GetString() == "usage").ToList();
        var one = Assert.Single(usage, l => l.GetProperty("route").GetString()!.StartsWith("GET api/hosts/{id", StringComparison.Ordinal));
        Assert.Equal(2, one.GetProperty("uses").GetInt64());
        Assert.Equal(1, one.GetProperty("users").GetInt32());
        Assert.Equal(2, one.GetProperty("clientErrors").GetInt64());
        var text = string.Join('\n', usage.Select(l => l.GetRawText()));
        Assert.DoesNotContain(id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_monitor_writes_how_the_server_is_doing()
    {
        await Monitor().TickAsync(Ct);

        var lines = Lines();
        var start = lines.First(l => l.GetProperty("type").GetString() == "start");
        Assert.StartsWith("1.0.0", start.GetProperty("version").GetString(), StringComparison.Ordinal);
        var hourly = lines.Last(l => l.GetProperty("type").GetString() == "hourly");
        Assert.True(hourly.GetProperty("database").GetProperty("megabytes").GetInt64() > 0);
        Assert.True(hourly.GetProperty("database").GetProperty("users").GetInt32() >= 1);
        Assert.NotEmpty(hourly.GetProperty("database").GetProperty("largestTables").EnumerateObject());
    }

    private DiagnosticsMonitor Monitor() => _factory.Services.GetServices<IHostedService>().OfType<DiagnosticsMonitor>().Single();
}
