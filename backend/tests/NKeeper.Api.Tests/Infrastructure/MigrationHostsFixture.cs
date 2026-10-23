using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// Two OpenSSH servers made to look like KVM hosts sharing DRBD resource r0, with a file system on
/// it holding VM "web"'s disk: kvm-a runs the VM with r0 Primary and mounted on /srv/vms; kvm-b
/// has r0 Secondary. Fake virsh, drbdadm, drbdsetup, mount, umount, mountpoint, findmnt and lsblk
/// keep their state in files, and refuse what the real ones would (mounting a Secondary, unmounting
/// what a running VM uses, demoting what is mounted, starting a VM without its disk). The fakes need
/// root, which the test user gets with sudo. Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class MigrationHostsFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Migration-Test-Passw0rd";
    public const string Address = "127.0.0.1";
    public const string VmUuid = "6f1c2a52-3b6e-4b8a-9d1e-2f0a7c4e9b11";

    /// <summary>VM "local", running on kvm-a from an image file on kvm-a's own disk.</summary>
    public const string LocalVmUuid = "0c8a4f3e-5d21-4b7a-8e6f-1a2b3c4d5e6f";

    public const string LocalImage = "/var/lib/libvirt/images/local.qcow2";
    private const int SshPort = 2222;

    private const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    private static readonly string[] Tools = ["virsh", "drbdadm", "drbdsetup", "mount", "umount", "mountpoint", "findmnt", "lsblk", "qemu-img"];

    public string? UnavailableReason { get; private set; }

    public IContainer Source { get; private set; } = null!;

    public IContainer Target { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            Source = await StartAsync("kvm-a");
            Target = await StartAsync("kvm-b");
            await ResetAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Migration tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public static int PortOf(IContainer container) => container.GetMappedPublicPort(SshPort);

    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    /// <summary>Puts both hosts back: the VM running on kvm-a from r0's file system, kvm-b ready to take it.</summary>
    public async Task ResetAsync()
    {
        const string common =
            "set -e; rm -rf /var/lib/fake-virsh /var/lib/fake-mount /var/lib/fake-drbd /var/lib/fake-disk /srv/vms /var/lib/libvirt/images"
            + " && mkdir -p /var/lib/fake-virsh /var/lib/fake-mount /var/lib/fake-drbd /var/lib/fake-disk/drbd0"
            + " && touch /var/lib/fake-mount/table && echo 'qcow2 image' > /var/lib/fake-disk/drbd0/web.qcow2"
            + " && { [ -b /dev/drbd0 ] || mknod /dev/drbd0 b 147 0; } && { [ -b /dev/sda2 ] || mknod /dev/sda2 b 8 2; }"
            + $" && echo '{UserName} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/nkeeper-test && chmod 440 /etc/sudoers.d/nkeeper-test";
        const string xml =
            $"<domain type='kvm'>\\n  <name>web</name>\\n  <uuid>{VmUuid}</uuid>\\n  <devices>\\n    <disk type='file' device='disk'>\\n      <source file='/srv/vms/web.qcow2'/>\\n      <target dev='vda'/>\\n    </disk>\\n  </devices>\\n</domain>\\n";
        const string localXml =
            $"<domain type='kvm'>\\n  <name>local</name>\\n  <uuid>{LocalVmUuid}</uuid>\\n  <devices>\\n    <disk type='file' device='disk'>\\n      <source file='{LocalImage}'/>\\n      <target dev='vda'/>\\n    </disk>\\n  </devices>\\n</domain>\\n";

        // The local VM's image: 3 MiB of data with a hole, more than one 1 MiB dd block and not a whole number of 64 MiB pieces.
        await ExecAsync(Source, common
            + $" && printf \"{localXml}\" > /var/lib/fake-virsh/{LocalVmUuid}.xml && echo running > /var/lib/fake-virsh/{LocalVmUuid}.state"
            + $" && mkdir -p /var/lib/libvirt/images && head -c 1048576 /dev/urandom > {LocalImage} && dd if=/dev/urandom of={LocalImage} bs=1024 count=1500 seek=2048 conv=notrunc 2>/dev/null"
            + $" && printf \"{xml}\" > /var/lib/fake-virsh/{VmUuid}.xml && echo running > /var/lib/fake-virsh/{VmUuid}.state"
            + " && mkdir -p /srv/vms && cp /var/lib/fake-disk/drbd0/web.qcow2 /srv/vms/ && echo '/dev/drbd0 /srv/vms ext4 1111-2222' > /var/lib/fake-mount/table"
            + " && echo Primary > /var/lib/fake-drbd/r0 && chmod -R a+rX /var/lib/fake-virsh /var/lib/fake-mount");
        await ExecAsync(Target, common + " && echo Secondary > /var/lib/fake-drbd/r0 && chmod -R a+rX /var/lib/fake-virsh /var/lib/fake-mount");
    }

    /// <summary>Runs a shell command on a host as root and returns its output.</summary>
    public static async Task<string> ExecAsync(IContainer container, string script)
    {
        var result = await container.ExecAsync(["sh", "-c", script]);
        return result.ExitCode == 0 ? result.Stdout : throw new InvalidOperationException($"“{script}” failed: {result.Stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in new[] { Source, Target })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    private static async Task<IContainer> StartAsync(string hostname)
    {
        var container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls239")
            .WithHostname(hostname)
            .WithEnvironment("PASSWORD_ACCESS", "true")
            .WithEnvironment("USER_NAME", UserName)
            .WithEnvironment("USER_PASSWORD", Password)
            .WithPortBinding(SshPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
            .Build();
        await container.StartAsync();
        foreach (var tool in Tools)
        {
            var script = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Migration", $"fake-{tool}.sh"));
            await container.CopyAsync(script, $"/usr/local/bin/{tool}", fileMode: Executable);
            await container.CopyAsync(script, $"/usr/local/sbin/{tool}", fileMode: Executable);
        }

        return container;
    }
}
