using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.SshServerFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A real OpenSSH server (linuxserver/openssh-server in Docker) for terminal session tests.
/// It accepts the password <see cref="Password"/> and the generated <see cref="PrivateKey"/>
/// for <see cref="UserName"/>. Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class SshServerFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Ssh-Test-Passw0rd";
    private const int SshPort = 2222;

    private IContainer? _container;

    public string? UnavailableReason { get; private set; }

    public const string Address = "127.0.0.1";

    public int Port => _container!.GetMappedPublicPort(SshPort);

    /// <summary>An unencrypted OpenSSH ed25519 private key authorized for <see cref="UserName"/>.</summary>
    public string PrivateKey { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls239")
                .WithEnvironment("PASSWORD_ACCESS", "true")
                .WithEnvironment("USER_NAME", UserName)
                .WithEnvironment("USER_PASSWORD", Password)
                .WithPortBinding(SshPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
                .Build();
            await _container.StartAsync();

            var keygen = await _container.ExecAsync([
                "sh", "-c",
                "ssh-keygen -q -t ed25519 -N '' -f /tmp/nkeeper_test_key"
                + " && cat /tmp/nkeeper_test_key.pub >> /config/.ssh/authorized_keys"
                + $" && chown {UserName} /config/.ssh/authorized_keys && chmod 600 /config/.ssh/authorized_keys"
                + " && cat /tmp/nkeeper_test_key",
            ]);
            if (keygen.ExitCode != 0)
            {
                throw new InvalidOperationException($"Generating a test key failed: {keygen.Stderr}");
            }

            PrivateKey = keygen.Stdout;
            await PrepareKvmAsync(_container);
        }
#pragma warning disable CA1031 // Any failure here means "no SSH server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"SSH terminal tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Port of the stand-in SPICE display (an echo server) on the SSH server's loopback.</summary>
    public const int FakeSpicePort = 5930;

    /// <summary>
    /// Makes the SSH server look like a KVM host: a fake <c>virsh</c>, TCP forwarding enabled (as
    /// virt-manager needs too), and an echo server standing in for a VM's SPICE port on loopback.
    /// </summary>
    private static async Task PrepareKvmAsync(IContainer container)
    {
        var virsh = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Kvm", "fake-virsh.sh"));
        await container.CopyAsync(virsh, "/usr/local/bin/virsh", fileMode: DotNet.Testcontainers.Configurations.UnixFileModes.UserRead | DotNet.Testcontainers.Configurations.UnixFileModes.UserWrite
            | DotNet.Testcontainers.Configurations.UnixFileModes.UserExecute | DotNet.Testcontainers.Configurations.UnixFileModes.GroupRead
            | DotNet.Testcontainers.Configurations.UnixFileModes.GroupExecute | DotNet.Testcontainers.Configurations.UnixFileModes.OtherRead
            | DotNet.Testcontainers.Configurations.UnixFileModes.OtherExecute);
        var forwarding = await container.ExecAsync([
            "sh", "-c",
            "sed -i 's/^AllowTcpForwarding no/AllowTcpForwarding yes/' /config/sshd/sshd_config && s6-svc -r /run/service/svc-openssh-server",
        ]);
        if (forwarding.ExitCode != 0)
        {
            throw new InvalidOperationException($"Enabling TCP forwarding failed: {forwarding.Stderr}");
        }

        // One connection at a time: OpenBSD netcat has no -e, so loop a FIFO through cat.
        _ = container.ExecAsync([
            "sh", "-c",
            $"while true; do rm -f /tmp/echo.fifo; mkfifo /tmp/echo.fifo; nc -l 127.0.0.1 {FakeSpicePort} < /tmp/echo.fifo | cat > /tmp/echo.fifo; done",
        ]);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
