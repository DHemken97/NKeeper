using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// An OpenSSH server made to look like a Linux host with a journal and SELinux: fake <c>journalctl</c>,
/// <c>getenforce</c>, <c>semodule</c>, <c>ausearch</c>, <c>audit2allow</c>, <c>checkmodule</c> and
/// <c>semodule_package</c>, answering in the real tools' formats. Like the real ones, those that read the
/// system need root, which the (non-root) test user gets with sudo. Tests are skipped, not passed, when
/// Docker is unavailable.
/// </summary>
public sealed class LinuxToolsServerFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Linux-Test-Passw0rd";
    public const string Address = "127.0.0.1";
    private const int SshPort = 2222;

    private const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    private IContainer? _container;

    public string? UnavailableReason { get; private set; }

    public int Port => _container!.GetMappedPublicPort(SshPort);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls239")
                .WithHostname("linux-test")
                .WithEnvironment("PASSWORD_ACCESS", "true")
                .WithEnvironment("USER_NAME", UserName)
                .WithEnvironment("USER_PASSWORD", Password)
                .WithPortBinding(SshPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
                .Build();
            await _container.StartAsync();

            foreach (var tool in new[] { "journalctl", "getenforce", "semodule", "ausearch", "audit2allow", "checkmodule", "semodule_package" })
            {
                await _container.CopyAsync(
                    await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Linux", $"fake-{tool}.sh")), $"/usr/local/bin/{tool}", fileMode: Executable);
            }

            await ResetAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Linux tool tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    /// <summary>Puts the journal, SELinux and the test user's sudo back as they were.</summary>
    public async Task ResetAsync()
    {
        var setup = await _container!.ExecAsync([
            "sh", "-c",
            "set -e; rm -rf /var/lib/fake-linux; mkdir -p /var/lib/fake-linux; chmod 777 /var/lib/fake-linux; cd /var/lib/fake-linux"
            + " && : > journal && : > journal-new && : > audit.log && : > journalctl-args && : > ausearch-args && chmod 666 journalctl-args ausearch-args"
            + " && echo Enforcing > mode && printf '100 base\\n100 httpd\\n400 my_old_fix\\n' > modules"
            + $" && echo '{UserName} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/nkeeper-test && chmod 440 /etc/sudoers.d/nkeeper-test",
        ]);
        if (setup.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preparing the fake host failed: {setup.Stderr}");
        }
    }

    /// <summary>Runs a shell script on the host as root; returns its exit code.</summary>
    public async Task<long> ExecAsync(string script) => (await _container!.ExecAsync(["sh", "-c", script])).ExitCode ?? -1;

    /// <summary>Puts a file in the fake tools' folder.</summary>
    public Task WriteAsync(string name, string content) =>
        _container!.CopyAsync(System.Text.Encoding.UTF8.GetBytes(content), $"/var/lib/fake-linux/{name}", fileMode: Executable);

    /// <summary>Reads a file from the fake tools' folder.</summary>
    public async Task<string> ReadAsync(string name) => (await _container!.ExecAsync(["cat", $"/var/lib/fake-linux/{name}"])).Stdout;

    /// <summary>Takes away the test user's sudo, as on a host where it needs a password.</summary>
    public async Task RemoveSudoAsync()
    {
        await _container!.ExecAsync(["rm", "-f", "/etc/sudoers.d/nkeeper-test"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
