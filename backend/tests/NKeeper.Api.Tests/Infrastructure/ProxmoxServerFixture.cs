using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// An OpenSSH server made to look like a Proxmox VE node named pve-test: a cluster file system
/// (<c>/etc/pve/nodes</c>) with VM configurations, and fake <c>pvesh</c>, <c>qm</c> and cluster
/// <c>ssh</c>. Like the real tools, the fakes need root, which the (non-root) test user gets with
/// sudo. Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class ProxmoxServerFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Pve-Test-Passw0rd";
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
                .WithHostname("pve-test")
                .WithEnvironment("PASSWORD_ACCESS", "true")
                .WithEnvironment("USER_NAME", UserName)
                .WithEnvironment("USER_PASSWORD", Password)
                .WithPortBinding(SshPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
                .Build();
            await _container.StartAsync();

            foreach (var (file, path) in new[] { ("fake-pvesh.sh", "/usr/local/bin/pvesh"), ("fake-qm.sh", "/usr/local/bin/qm"), ("fake-ssh.sh", "/usr/local/bin/ssh") })
            {
                await _container.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Kvm", file)), path, fileMode: Executable);
            }

            // VM 100 has a standard display (a snapshot of it had a serial one), 102 a serial
            // terminal, 200 (on pve2) no vga line at all. Configurations are root's only, as on Proxmox.
            var setup = await _container.ExecAsync([
                "sh", "-c",
                "set -e; d=/etc/pve/nodes; mkdir -p $d/pve-test/qemu-server $d/pve2/qemu-server"
                + " && printf 'name: web\\nvga: std,memory=32\\n\\n[before]\\nvga: serial0\\n' > $d/pve-test/qemu-server/100.conf"
                + " && printf 'name: db\\n' > $d/pve-test/qemu-server/101.conf"
                + " && printf 'name: serial-vm\\nvga: serial0\\n' > $d/pve-test/qemu-server/102.conf"
                + " && printf 'name: remote\\n' > $d/pve2/qemu-server/200.conf"
                + " && touch $d/pve2/ssh_known_hosts && chmod 640 $d/*/qemu-server/*.conf"
                + $" && echo '{UserName} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/nkeeper-test && chmod 440 /etc/sudoers.d/nkeeper-test",
            ]);
            if (setup.ExitCode != 0)
            {
                throw new InvalidOperationException($"Preparing the fake Proxmox node failed: {setup.Stderr}");
            }
        }
#pragma warning disable CA1031 // Any failure here means "no server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Proxmox tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
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
