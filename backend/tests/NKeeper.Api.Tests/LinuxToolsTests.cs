using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Api.Journal;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Selinux;
using NKeeper.Api.Tests.Infrastructure;

namespace NKeeper.Api.Tests;

/// <summary>What the journal viewer and SELinux manager ask of a host, and what they make of the answers, in the real tools' formats.</summary>
public sealed class LinuxToolParsingTests
{
    private const string Line = """{"__REALTIME_TIMESTAMP":"1793992564454931","PRIORITY":"4","_PID":"39","SYSLOG_IDENTIFIER":"nktest","_COMM":"logger","_HOSTNAME":"web1","MESSAGE":"event 5","__CURSOR":"s=de0;i=5df;b=52f;m=f8;t=65f;x=288","_SYSTEMD_UNIT":"nginx.service"}""";

    [Fact]
    public void JournalEntry_IsReadFromJson()
    {
        var entry = JournalHost.ParseEntry(Line)!;

        Assert.Equal("s=de0;i=5df;b=52f;m=f8;t=65f;x=288", entry.Cursor);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 19, 16, 4, TimeSpan.Zero).AddTicks(4549310), entry.Time);
        Assert.Equal((4, "nginx.service", "nktest", "39", "web1", "event 5"), (entry.Priority, entry.Unit, entry.Identifier, entry.Pid, entry.Hostname, entry.Message));
    }

    [Theory]
    [InlineData("""{"__CURSOR":"c","MESSAGE":[98,97,100,32,255,32,98]}""", "bad \uFFFD b")] // Not valid UTF-8.
    [InlineData("""{"__CURSOR":"c","MESSAGE":["one","two"]}""", "one")] // The field more than once.
    [InlineData("""{"__CURSOR":"c","MESSAGE":"\u001b[31mred\u001b[0m"}""", "red")] // Colours.
    [InlineData("""{"__CURSOR":"c"}""", "")]
    public void JournalMessages_OfOtherShapes_AreReadable(string line, string message) =>
        Assert.Equal(message, JournalHost.ParseEntry(line)!.Message);

    [Fact]
    public void AJournalMessageOver4KB_SaysSo() =>
        Assert.Contains("over 4 KB", JournalHost.ParseEntry("""{"__CURSOR":"c","MESSAGE":null}""")!.Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"MESSAGE":"no cursor"}""")]
    [InlineData("[1,2]")]
    public void WhatIsntAJournalEntry_IsSkipped(string line) => Assert.Null(JournalHost.ParseEntry(line));

    [Fact]
    public void JournalQuery_AsksForOnlyWhatsSet_NewestFirst()
    {
        var plain = JournalHost.ReadScript(new JournalQuery(null, null, null, null, null, null, null));
        Assert.EndsWith("$p journalctl --no-pager --output=json --reverse --lines=201", plain, StringComparison.Ordinal);

        var full = JournalHost.ReadScript(new JournalQuery(
            DateTimeOffset.FromUnixTimeSeconds(100), DateTimeOffset.FromUnixTimeSeconds(200), 3, "nginx.service", "sshd", "failed", "s=1;i=2"));
        Assert.EndsWith(
            "--lines=201 --since=@100 --until=@200 --priority=3 --unit='nginx.service' --identifier='sshd' --grep='failed' --after-cursor='s=1;i=2'",
            full,
            StringComparison.Ordinal);
    }

    [Fact]
    public void JournalValues_StayOneWord_AfterTheirOption()
    {
        var script = JournalHost.ReadScript(new JournalQuery(null, null, null, "x'; reboot; '", "-f", "$(reboot)", null));

        Assert.Contains(@"--unit='x'\''; reboot; '\'''", script, StringComparison.Ordinal);
        Assert.Contains("--identifier='-f'", script, StringComparison.Ordinal);
        Assert.Contains("--grep='$(reboot)'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Following_HasNoTimeLimits_AndStartsNowOrFromTheCursor()
    {
        var times = DateTimeOffset.FromUnixTimeSeconds(100);

        var now = JournalHost.FollowScript(new JournalQuery(times, times, 3, null, null, null, null));
        Assert.EndsWith("exec $p journalctl --no-pager --output=json --follow --lines=0 --priority=3", now, StringComparison.Ordinal);

        var from = JournalHost.FollowScript(new JournalQuery(null, null, null, null, null, null, "s=1"));
        Assert.EndsWith("--follow --after-cursor='s=1'", from, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("s=de0;i=5df;b=52f;m=f8;t=65f;x=288", true)]
    [InlineData(null, true)]
    [InlineData("s=1'; reboot", false)]
    [InlineData("", false)]
    public void JournalCursors_AreChecked(string? cursor, bool valid) => Assert.Equal(valid, JournalHost.IsCursor(cursor));

    [Theory]
    [InlineData("journalctl: unrecognized option '--grep=x'", "needs systemd 237")]
    [InlineData("No journal files were opened due to insufficient permissions.", "systemd-journal group")]
    [InlineData("Bad pattern \"(\": missing closing parenthesis", "isn't a valid regular expression")]
    [InlineData("Something else", "Something else")]
    public void JournalErrors_SayWhatToDo(string message, string expected) =>
        Assert.Contains(expected, JournalHost.Explain(message), StringComparison.Ordinal);

    [Fact]
    public void Denial_IsReadFromAnAvcRecord()
    {
        var denial = SelinuxHost.ParseDenial(
            """type=AVC msg=audit(1730000000.123:456): avc:  denied  { read write } for  pid=123 comm="nginx" name="index.html" dev="dm-0" ino=99 scontext=system_u:system_r:httpd_t:s0 tcontext=unconfined_u:object_r:user_home_t:s0 tclass=file permissive=0""")!;

        Assert.Equal((456, "AVC", true), (denial.EventId, denial.Type, denial.Blocked));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1730000000123), denial.Time);
        Assert.Equal(["read", "write"], denial.Permissions);
        Assert.Equal(("nginx", "123", "index.html", "file"), (denial.Process, denial.Pid, denial.Target, denial.TargetClass));
        Assert.Equal(("system_u:system_r:httpd_t:s0", "unconfined_u:object_r:user_home_t:s0"), (denial.SourceContext, denial.TargetContext));
    }

    [Fact]
    public void Denial_WithoutQuotes_AsAusearchMayPrint_IsReadTheSame()
    {
        var denial = SelinuxHost.ParseDenial(
            "type=AVC msg=audit(1730000000.123:456): avc:  denied  { read } for  pid=123 comm=nginx name=index.html dev=dm-0 ino=99 scontext=a:b:c:s0 tcontext=d:e:f:s0 tclass=file permissive=0")!;

        Assert.Equal(("nginx", "index.html"), (denial.Process, denial.Target));
    }

    [Fact]
    public void Denial_FromSystemd_IsReadFromAUserAvcRecord()
    {
        var denial = SelinuxHost.ParseDenial(
            """type=USER_AVC msg=audit(1730000100.500:457): pid=1 uid=0 auid=4294967295 ses=4294967295 subj=system_u:system_r:init_t:s0 msg='avc:  denied  { start } for auid=n/a uid=0 gid=0 path="/usr/lib/systemd/system/x.service" cmdline="" function="mac_selinux_filter" scontext=system_u:system_r:init_t:s0 tcontext=system_u:object_r:etc_t:s0 tclass=service permissive=1 exe="/usr/lib/systemd/systemd" sauid=0 hostname=? addr=? terminal=?'""")!;

        Assert.Equal(("USER_AVC", false, "/usr/lib/systemd/systemd", "/usr/lib/systemd/system/x.service", "service"), (denial.Type, denial.Blocked, denial.Process, denial.Target, denial.TargetClass));
        Assert.Equal(["start"], denial.Permissions);
    }

    [Fact]
    public void WhatIsGranted_IsntADenial_ButAnSelinuxErrorIs()
    {
        Assert.Null(SelinuxHost.ParseDenial("type=AVC msg=audit(1730000200.100:458): avc:  granted  { setenforce } for  pid=9 comm=\"setenforce\" scontext=x:y:z:s0 tcontext=x:y:z:s0 tclass=security"));

        var error = SelinuxHost.ParseDenial("type=SELINUX_ERR msg=audit(1730000300.000:459): op=security_compute_sid invalid_context=x:y:z:s0")!;
        Assert.Equal(("SELINUX_ERR", 459), (error.Type, error.EventId));
        Assert.Empty(error.Permissions);
    }

    [Theory]
    [InlineData("module nkfix 1.0;\n\nrequire {\n}\n", "nkfix")]
    [InlineData("\n# A comment first\n\nmodule my_fix 1.2.3 ;\n", "my_fix")]
    [InlineData("policy_module(foo, 1.0)\n", null)] // Needs the policy's development Makefile.
    [InlineData("allow a b:c d;\n", null)]
    [InlineData("# only comments\n", null)]
    public void APolicysName_ComesFromItsSource(string source, string? name) => Assert.Equal(name, SelinuxHost.DeclaredModule(source));

    [Theory]
    [InlineData("nginx_fix", true)]
    [InlineData("A1", true)]
    [InlineData("1abc", false)]
    [InlineData("bad name", false)]
    [InlineData("x;reboot", false)]
    [InlineData("-f", false)]
    [InlineData("../x", false)]
    [InlineData("", false)]
    public void PolicyNames_AreChecked(string name, bool valid) => Assert.Equal(valid, SelinuxHost.IsModuleName(name));

    [Fact]
    public void Status_TurnsItsTimes_IntoTheHostsOwnFormat()
    {
        var script = SelinuxHost.StatusScript(DateTimeOffset.FromUnixTimeSeconds(100), DateTimeOffset.FromUnixTimeSeconds(200));

        Assert.Contains("--raw -ts $(LC_ALL=C date -d @100 '+%x %H:%M:%S') -te $(LC_ALL=C date -d @200 '+%x %H:%M:%S')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingAPolicy_CarriesItsSourceSafely()
    {
        const string source = "module x 1.0;\n# '; reboot; '\n";

        var script = SelinuxHost.InstallScript("x", source);

        Assert.DoesNotContain("reboot", script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source)), script, StringComparison.Ordinal);
    }
}

/// <summary>The journal and SELinux over SSH, against (fake) tools run with sudo.</summary>
public sealed class LinuxToolsTests(PostgresServerFixture postgres, LinuxToolsServerFixture linux) : IAsyncLifetime, IClassFixture<LinuxToolsServerFixture>
{
    private const string Avc = """type=AVC msg=audit(1730000000.123:456): avc:  denied  { read } for  pid=123 comm="nginx" name="index.html" dev="dm-0" ino=99 scontext=system_u:system_r:httpd_t:s0 tcontext=unconfined_u:object_r:user_home_t:s0 tclass=file permissive=0""";

    private const string Policy = "module nginx_fix 1.0;\n\nrequire {\n\ttype httpd_t;\n}\n";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private NKeeperApiFactory _factory = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Entry(string cursor, string message, int priority = 6, long micros = 1793992564454931) =>
        $$"""{"__CURSOR":"{{cursor}}","__REALTIME_TIMESTAMP":"{{micros}}","PRIORITY":"{{priority}}","SYSLOG_IDENTIFIER":"sshd","MESSAGE":"{{message}}"}""" + "\n";

    public async ValueTask InitializeAsync()
    {
        linux.SkipIfUnavailable();
        await linux.ResetAsync();
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
    public async Task Journal_AsksToTrustTheHostKey_ThenReadsEntries_WithSudo_AndAudits()
    {
        await linux.WriteAsync("journal", Entry("s=2", "newest", 3) + Entry("s=1", "older"));
        var host = await CreateHostAsync();

        var first = await PostAsync("/api/journal/entries", Login(host));
        Assert.Equal("trustRequired", first.GetProperty("status").GetString());
        var answer = await PostAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = first.GetProperty("hostKey").GetProperty("fingerprint").GetString() });

        var page = answer.GetProperty("page");
        Assert.Equal(["newest", "older"], page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("message").GetString()));
        Assert.Equal(3, page.GetProperty("entries")[0].GetProperty("priority").GetInt32());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.False(page.GetProperty("limited").GetBoolean());

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=journal", Ct);
        Assert.Equal("journal.read", Assert.Single(audit.GetProperty("events").EnumerateArray()).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Journal_PassesItsFilters_AndPagingBackIsntAudited()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("journal", Entry("s=1", "needle here"));

        var response = await PostAsync("/api/journal/entries", new
        {
            hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id,
            since = "2026-11-06T18:00:00Z", until = "2026-11-06T19:00:00Z", maxPriority = 3, unit = "ssh.service", identifier = "sshd", search = "needle", after = "s=9;i=1",
        });

        Assert.Equal("needle here", response.GetProperty("page").GetProperty("entries")[0].GetProperty("message").GetString());
        var args = (await linux.ReadAsync("journalctl-args")).Trim().Split('\n')[^1];
        Assert.Contains($"--since=@{new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()}", args, StringComparison.Ordinal);
        Assert.Contains($"--until=@{new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()}", args, StringComparison.Ordinal);
        Assert.Contains("--priority=3 --unit=ssh.service --identifier=sshd --grep=needle --after-cursor=s=9;i=1", args, StringComparison.Ordinal);

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=journal", Ct);
        Assert.Single(audit.GetProperty("events").EnumerateArray()); // Only the read that trusted the key.
    }

    [Fact]
    public async Task Journal_OverAPageSaysMoreRemain_AndKeepsToThePage()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("journal", string.Concat(Enumerable.Range(0, JournalHost.PageSize + 1).Select(i => Entry($"s={i}", $"entry {i}"))));

        var page = (await PostAsync("/api/journal/entries", Login(host))).GetProperty("page");

        Assert.Equal(JournalHost.PageSize, page.GetProperty("entries").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Journal_SearchMatchingNothing_IsNoEntries_NotAnError()
    {
        var host = await CreateTrustedHostAsync();

        var page = (await PostAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, search = "nothing" })).GetProperty("page");

        Assert.Equal(0, page.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Journal_ABadRegularExpression_SaysSo()
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, search = "(" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("valid regular expression", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_StartAfterStop_IsRefused()
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, since = "2026-11-06T19:00:00Z", until = "2026-11-06T18:00:00Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Journal_ABadCursor_IsRefused_BeforeItReachesTheHost()
    {
        var host = await CreateTrustedHostAsync();
        var before = await linux.ReadAsync("journalctl-args");

        var response = await SendAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, after = "x'; reboot" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await linux.ReadAsync("journalctl-args"));
    }

    [Fact]
    public async Task Journal_WithoutSudo_SaysWhenItCantReadIt_OrReadsPartOfIt()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("journal", Entry("s=1", "mine"));
        await linux.RemoveSudoAsync();

        var refused = await SendAsync("/api/journal/entries", Login(host));
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains("systemd-journal group", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.Equal(0, await linux.ExecAsync("touch /var/lib/fake-linux/journal-open"));
        var page = (await PostAsync("/api/journal/entries", Login(host))).GetProperty("page");
        Assert.True(page.GetProperty("limited").GetBoolean());
        Assert.Equal("mine", page.GetProperty("entries")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Following_StreamsNewEntries_FromTheCursor_AndStopsWithTheTab()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("journal-new", Entry("s=5", "just happened"));
        var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "journal", path: "follow");

        await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, after = "s=4", maxPriority = 3 }, Ct);
        await terminal.ExpectAsync("connected", Ct);
        var entry = await terminal.ExpectAsync("entry", Ct);

        Assert.Equal("just happened", entry.GetProperty("entry").GetProperty("message").GetString());
        var args = (await linux.ReadAsync("journalctl-args")).Trim().Split('\n')[^1];
        Assert.Contains("--follow --priority=3 --after-cursor=s=4", args, StringComparison.Ordinal);

        await terminal.DisposeAsync();
        // Dropping the connection ends journalctl (sudo's child, here a sleep).
        for (var i = 0; i < 40 && await linux.ExecAsync("pgrep -f 'sleep 300' > /dev/null") == 0; i++)
        {
            await Task.Delay(250, Ct);
        }

        Assert.NotEqual(0, await linux.ExecAsync("pgrep -f 'sleep 300' > /dev/null"));
    }

    [Fact]
    public async Task Following_BeforeTheKeyIsTrusted_SaysSo()
    {
        var host = await CreateHostAsync();
        await using var terminal = await TerminalClient.ConnectAsync(_factory, _cookies, Ct, protocol: "journal", path: "follow");

        await terminal.SendJsonAsync(new { type = "start", hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id }, Ct);

        Assert.Equal(SessionErrorCodes.HostKeyRejected, (await terminal.ExpectAsync("error", Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Selinux_ShowsItsModeModulesAndDenials_InOnePlace()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("audit.log", Avc + "\ntype=AVC msg=audit(1730000900.000:460): avc:  denied  { write } for  pid=9 comm=\"cat\" name=\"x\" scontext=a:b:c:s0 tcontext=d:e:f:s0 tclass=file permissive=1\n");

        var selinux = (await PostAsync("/api/selinux/status", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, since = "2024-01-01T00:00:00Z", until = "2030-01-01T00:00:00Z" })).GetProperty("selinux");

        Assert.Equal("Enforcing", selinux.GetProperty("mode").GetString());
        Assert.Equal(["base", "httpd", "my_old_fix"], selinux.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(400, selinux.GetProperty("modules")[2].GetProperty("priority").GetInt32());

        // Newest first; the permissive one was logged, not blocked.
        var denials = selinux.GetProperty("denials");
        Assert.Equal([460L, 456L], denials.EnumerateArray().Select(d => d.GetProperty("eventId").GetInt64()));
        Assert.False(denials[0].GetProperty("blocked").GetBoolean());
        Assert.True(denials[1].GetProperty("blocked").GetBoolean());

        var args = (await linux.ReadAsync("ausearch-args")).Trim().Split('\n')[^1];
        Assert.Matches(@"-ts \d\d/\d\d/\d\d \d\d:\d\d:\d\d -te \d\d/\d\d/\d\d \d\d:\d\d:\d\d", args);
    }

    [Fact]
    public async Task Selinux_WithNoDenials_IsNotAProblem_ButNoAuditLogIs()
    {
        var host = await CreateTrustedHostAsync();

        var none = (await PostAsync("/api/selinux/status", Login(host))).GetProperty("selinux");
        Assert.Equal(0, none.GetProperty("denials").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("auditProblem").ValueKind);

        Assert.Equal(0, await linux.ExecAsync("touch /var/lib/fake-linux/no-audit-log"));
        var noLog = (await PostAsync("/api/selinux/status", Login(host))).GetProperty("selinux");
        Assert.Contains("auditd", noLog.GetProperty("auditProblem").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selinux_OnAHostWithoutIt_SaysSo()
    {
        var host = await CreateTrustedHostAsync();
        Assert.Equal(0, await linux.ExecAsync("mv /usr/local/bin/getenforce /usr/local/bin/getenforce.off"));
        try
        {
            var selinux = (await PostAsync("/api/selinux/status", Login(host))).GetProperty("selinux");

            Assert.Equal(JsonValueKind.Null, selinux.GetProperty("mode").ValueKind);
        }
        finally
        {
            Assert.Equal(0, await linux.ExecAsync("mv /usr/local/bin/getenforce.off /usr/local/bin/getenforce"));
        }
    }

    [Fact]
    public async Task Selinux_WithoutRoot_SaysItsNeeded()
    {
        var host = await CreateTrustedHostAsync();
        await linux.RemoveSudoAsync();

        var response = await SendAsync("/api/selinux/status", Login(host));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("needs root", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selinux_GeneratesAPolicyFromDenials_ThenInstallsAndRemovesIt_AndAudits()
    {
        var host = await CreateTrustedHostAsync();
        await linux.WriteAsync("audit.log", Avc + "\n");

        var generated = await PostAsync("/api/selinux/generate", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "nginx_fix", eventIds = new[] { 456 } });
        var source = generated.GetProperty("source").GetString()!;
        Assert.Contains("module nginx_fix 1.0;", source, StringComparison.Ordinal);
        Assert.Contains("allow httpd_t user_home_t:file read;", source, StringComparison.Ordinal);
        // Only written so far: nothing's installed until the user has read it.
        Assert.DoesNotContain("nginx_fix", await linux.ReadAsync("modules"), StringComparison.Ordinal);

        var installed = await PostAsync("/api/selinux/install", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, source });
        Assert.Equal("Installed nginx_fix.", installed.GetProperty("message").GetString());
        Assert.Contains("400 nginx_fix", await linux.ReadAsync("modules"), StringComparison.Ordinal);

        var removed = await PostAsync("/api/selinux/remove", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "nginx_fix", priority = 400 });
        Assert.Equal("Removed nginx_fix.", removed.GetProperty("message").GetString());
        Assert.DoesNotContain("nginx_fix", await linux.ReadAsync("modules"), StringComparison.Ordinal);

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=selinux", Ct);
        Assert.Equal(["selinux.remove", "selinux.install"], audit.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task Selinux_GeneratingFromEventsThatAreGone_SaysSo()
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/selinux/generate", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "nginx_fix", eventIds = new[] { 999 } });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("found nothing to allow", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nginx;reboot")]
    [InlineData("-f")]
    public async Task Selinux_ABadPolicyName_IsRefused(string name)
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/selinux/generate", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name, eventIds = new[] { 456 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Selinux_APolicyThatWontCompile_ReportsWhy_AndChangesNothing()
    {
        var host = await CreateTrustedHostAsync();
        var before = await linux.ReadAsync("modules");

        var response = await SendAsync("/api/selinux/install", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, source = "module broken 1.0;\nrequire { type x_t" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("syntax error", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(before, await linux.ReadAsync("modules"));
    }

    [Fact]
    public async Task Selinux_APolicyWithoutAName_IsRefused()
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/selinux/install", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, source = "allow a b:c d;" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Selinux_InstallIsJudgedByTheHostsList_NotTheExitCode()
    {
        var host = await CreateTrustedHostAsync();
        Assert.Equal(0, await linux.ExecAsync("touch /var/lib/fake-linux/silent-install"));

        var response = await SendAsync("/api/selinux/install", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, source = Policy });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("doesn't list nginx_fix", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit/events?category=selinux", Ct);
        Assert.Empty(audit.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task Selinux_WontOverrideOrRemove_TheDistributionsModules()
    {
        var host = await CreateTrustedHostAsync();
        var before = await linux.ReadAsync("modules");

        var install = await SendAsync("/api/selinux/install", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, source = "module httpd 1.0;\n" });
        Assert.Equal(HttpStatusCode.BadRequest, install.StatusCode);
        Assert.Contains("distribution", await install.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var remove = await SendAsync("/api/selinux/remove", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "base", priority = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);

        Assert.Equal(before, await linux.ReadAsync("modules"));
    }

    [Fact]
    public async Task Selinux_RemovesOnlyWhatTheHostLists()
    {
        var host = await CreateTrustedHostAsync();

        var response = await SendAsync("/api/selinux/remove", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "x'; reboot; '", priority = 400 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no module", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserWithoutAccessToTheHost_IsToldItDoesntExist()
    {
        var host = await CreateTrustedHostAsync();
        await _factory.CreateUserAsync("bob", "bob@nkeeper.test", "Bob-Passw0rd-123");
        using var bob = _factory.CreateClient();
        (await bob.LoginAsync("bob", "Bob-Passw0rd-123", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendJsonAsync(HttpMethod.Post, "/api/journal/entries", Login(host), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendJsonAsync(HttpMethod.Post, "/api/selinux/remove", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, name = "my_old_fix", priority = 400 }, Ct)).StatusCode);
        Assert.Contains("my_old_fix", await linux.ReadAsync("modules"), StringComparison.Ordinal);
    }

    private static object Login(HostResponse host) => new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id };

    private Task<HttpResponseMessage> SendAsync(string url, object body) => _client.SendJsonAsync(HttpMethod.Post, url, body, Ct);

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var response = await SendAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HostResponse> CreateHostAsync()
    {
        var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"linux-{Guid.NewGuid():N}",
            ipAddress = LinuxToolsServerFixture.Address,
            services = new[] { new { protocol = "Ssh", port = (int?)linux.Port } },
            credentials = new[] { new { username = LinuxToolsServerFixture.UserName, password = LinuxToolsServerFixture.Password } },
        }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HostResponse>(Json, Ct))!;
    }

    private async Task<HostResponse> CreateTrustedHostAsync()
    {
        var host = await CreateHostAsync();
        var first = await PostAsync("/api/journal/entries", Login(host));
        await PostAsync("/api/journal/entries", new { hostId = host.Id, serviceIndex = 0, credentialId = host.Credentials[0].Id, trustFingerprint = first.GetProperty("hostKey").GetProperty("fingerprint").GetString() });
        return host;
    }
}
