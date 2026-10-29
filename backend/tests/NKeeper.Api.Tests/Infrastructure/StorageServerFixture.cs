using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// An OpenSSH server made to look like a storage host: fake <c>iscsiadm</c> (two known targets, one
/// logged in), <c>multipath</c> (two maps) and <c>drbdadm</c>/<c>drbdsetup</c> (r0 up, r1 down).
/// It's also Proxmox node san-test (of san-test and node2), with fake <c>pvesm</c> and LVM tools:
/// mpatha is blank, and the other map is volume group vgsan, storage san, used only by node2.
/// Like the real tools, the fakes need root, which the (non-root) test user gets with sudo. Actions
/// change the state, so tests <see cref="ResetAsync"/> it first. Tests are skipped, not passed,
/// when Docker is unavailable.
/// </summary>
public sealed class StorageServerFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Storage-Test-Passw0rd";
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
                .WithHostname("san-test")
                .WithEnvironment("PASSWORD_ACCESS", "true")
                .WithEnvironment("USER_NAME", UserName)
                .WithEnvironment("USER_PASSWORD", Password)
                .WithPortBinding(SshPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
                .Build();
            await _container.StartAsync();

            foreach (var tool in new[] { "iscsiadm", "multipath", "drbdadm", "drbdsetup", "pvesm" })
            {
                await _container.CopyAsync(
                    await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Storage", $"fake-{tool}.sh")), $"/usr/local/bin/{tool}", fileMode: Executable);
            }

            var lvm = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Storage", "fake-lvm.sh"));
            foreach (var tool in new[] { "pvs", "pvcreate", "vgcreate", "vgchange", "lvs", "blkid", "lsblk" })
            {
                await _container.CopyAsync(lvm, $"/usr/local/bin/{tool}", fileMode: Executable);
            }

            await ResetAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Storage tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    /// <summary>Puts the storage (and the test user's sudo) back as it was at the start.</summary>
    public async Task ResetAsync()
    {
        var setup = await _container!.ExecAsync([
            "sh", "-c",
            "set -e; rm -rf /var/lib/fake-iscsi /var/lib/fake-multipath /var/lib/fake-drbd /var/lib/fake-lvm /etc/pve"
            + " && mkdir -p /etc/iscsi /var/lib/fake-iscsi /var/lib/fake-multipath /var/lib/fake-drbd /var/lib/fake-lvm"
            + " /etc/pve/nodes/san-test/qemu-server /etc/pve/nodes/san-test/lxc /etc/pve/nodes/node2/qemu-server"
            + " && printf 'dir: local\\n\\tpath /var/lib/vz\\n\\nlvm: san\\n\\tvgname vgsan\\n\\tcontent rootdir,images\\n\\tnodes node2\\n\\tshared 1\\n' > /etc/pve/storage.cfg"
            + " && echo '/dev/mapper/36001405bbbbbbbbbbbbbbbbbbbbbbbbb|vgsan' > /var/lib/fake-lvm/pvs"
            + " && echo 'InitiatorName=iqn.1993-08.org.debian:01:san-test' > /etc/iscsi/initiatorname.iscsi && chmod 600 /etc/iscsi/initiatorname.iscsi"
            + " && printf '10.0.0.5:3260,1 iqn.2003-01.org.example.san:disk1\\n10.0.0.5:3260,1 iqn.2003-01.org.example.san:disk2\\n' > /var/lib/fake-iscsi/nodes"
            + " && echo '10.0.0.5:3260 iqn.2003-01.org.example.san:disk1' > /var/lib/fake-iscsi/sessions"
            + " && echo Secondary > /var/lib/fake-drbd/r0"
            + $" && echo '{UserName} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/nkeeper-test && chmod 440 /etc/sudoers.d/nkeeper-test",
        ]);
        if (setup.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preparing the fake storage host failed: {setup.Stderr}");
        }
    }

    /// <summary>Runs a shell script on the host as root; returns its exit code.</summary>
    public async Task<long> ExecAsync(string script) => (await _container!.ExecAsync(["sh", "-c", script])).ExitCode ?? -1;

    /// <summary>Makes iSCSI logins exit 0 without logging in.</summary>
    public async Task MakeLoginsSilentAsync()
    {
        await _container!.ExecAsync(["touch", "/var/lib/fake-iscsi/silent"]);
    }

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
