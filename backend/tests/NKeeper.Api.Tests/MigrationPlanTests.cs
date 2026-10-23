using NKeeper.Api.Migration;
using NKeeper.Api.Storage;

namespace NKeeper.Api.Tests;

/// <summary>How the planner moves a VM's storage: which steps, in which order, live or offline, and what blocks it.</summary>
public sealed class MigrationPlanTests
{
    private static readonly StorageMount VmsMount = new("/dev/drbd0", "/srv/vms", "ext4", "1111-2222");

    private static readonly GuestDisk OnDrbdFilesystem = new(
        "vda", "/srv/vms/web.qcow2", DiskLayouts.Filesystem, VmsMount, [new StorageLayer(LayerKinds.Drbd, "r0", "0")]);

    private static readonly GuestDisk RawDrbd = new("vda", "/dev/drbd1", DiskLayouts.Block, null, [new StorageLayer(LayerKinds.Drbd, "r1", "1")]);

    /// <summary>An LVM volume on a multipath map over two iSCSI sessions.</summary>
    private static readonly GuestDisk LvmOnIscsi = new(
        "vdb", "/dev/vg0/web", DiskLayouts.Block, null,
        [
            new StorageLayer(LayerKinds.Lvm, "vg0/web"),
            new StorageLayer(LayerKinds.Multipath, "36001405aaaa", "mpatha"),
            new StorageLayer(LayerKinds.Iscsi, "iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260"),
            new StorageLayer(LayerKinds.Iscsi, "iqn.2003-01.org.example.san:disk1", "10.0.1.5:3260"),
        ]);

    private static StorageStatus Drbd(params DrbdResource[] resources) => new(null, null, resources);

    private static DrbdResource Resource(string name, bool up = true, string disk = "UpToDate") =>
        new(name, up, up ? "Secondary" : null, up ? [new DrbdVolume(0, 0, disk)] : [], []);

    private static PlanInput Input(
        IReadOnlyList<GuestDisk> disks,
        StorageStatus? target = null,
        string via = "kvm",
        string state = "running",
        (bool, string?)? peer = null,
        IReadOnlyDictionary<(string, string), string>? inUse = null,
        IReadOnlyDictionary<string, LocalImage>? images = null) =>
        new(via, "web", false, state, "kvm-a", "kvm-b", disks, inUse ?? new Dictionary<(string, string), string>(),
            new StorageStatus(null, null, null), target ?? Drbd(Resource("r0"), Resource("r1")), peer ?? (true, null), (true, null), [], [], images);

    private static readonly GuestDisk LocalFile = new(
        "vda", "/var/lib/libvirt/images/web.qcow2", DiskLayouts.Local, new StorageMount("/dev/sda2", "/", "ext4", null), [new StorageLayer(LayerKinds.Local, "sda")]);

    private static Dictionary<string, LocalImage> Image(string? backing = null, bool exists = false, long free = 100L << 30, bool qemuImg = true) =>
        new() { [LocalFile.Path] = new LocalImage("qcow2", 20L << 30, 12L << 30, 12L << 30, backing, exists, free, qemuImg) };

    private static IEnumerable<string> Kinds(MigrationPlan plan) => plan.Steps.Select(s => $"{s.Side}:{s.Kind}");

    [Fact]
    public void FilesystemOnDrbd_IsMovedOffline_EvenWhenLiveIsAskedFor()
    {
        var plan = MigrationPlanner.Plan(Input([OnDrbdFilesystem]), new MigrationOptions { Live = true });

        Assert.Equal("offline", plan.Mode);
        Assert.False(plan.Choices.LiveAvailable);
        Assert.Contains(plan.Notes, n => n.StartsWith("It will be shut down for the move", StringComparison.Ordinal));
        Assert.Equal(
            [
                "target:drbd-ready", "source:guest-shutdown", "source:umount", "source:drbd-secondary",
                "target:drbd-primary", "target:mount", "target:check-paths", "hypervisor:guest-move", "target:guest-start",
            ],
            Kinds(plan));
        Assert.Equal("r0", plan.Steps.Single(s => s.Kind == StepKinds.Mount).Args["drbd"]);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public void RawDrbd_MovesLive_WithBothPrimariesOnlyDuringTheMove()
    {
        var plan = MigrationPlanner.Plan(Input([RawDrbd]), new MigrationOptions());

        Assert.Equal("live", plan.Mode);
        Assert.Equal(
            [
                "target:drbd-ready", "both:drbd-allow-two", "target:drbd-primary", "target:check-paths",
                "hypervisor:guest-move", "source:drbd-secondary", "both:drbd-disallow-two",
            ],
            Kinds(plan));
        Assert.Equal("1", plan.Steps.Single(s => s.Kind == StepKinds.GuestMove).Args["live"]);
    }

    [Fact]
    public void WithoutSshBetweenTheHosts_LibvirtMovesOffline_AndSaysWhy()
    {
        var plan = MigrationPlanner.Plan(Input([RawDrbd], peer: (false, "Permission denied (publickey).")), new MigrationOptions());

        Assert.Equal("offline", plan.Mode);
        Assert.Contains("Permission denied (publickey).", plan.Choices.LiveUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("source:guest-shutdown", Kinds(plan));
    }

    [Fact]
    public void Proxmox_NeedsSshBetweenItsNodes()
    {
        var plan = MigrationPlanner.Plan(Input([], via: "proxmox", peer: (false, "Host key verification failed.")), new MigrationOptions());

        Assert.Contains(plan.Blockers, b => b.Contains("Host key verification failed.", StringComparison.Ordinal));
    }

    [Fact]
    public void IscsiTheNewHostLacks_IsDiscoveredAndLoggedIn_AndTheOldHostLogsOut_ByDefaultOnKvm()
    {
        var target = new StorageStatus(new IscsiStatus(null, [], [new IscsiNode("iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260", false)]), [], null);

        var plan = MigrationPlanner.Plan(Input([LvmOnIscsi], target), new MigrationOptions { Live = false });

        Assert.Equal(
            [
                "target:iscsi-login", "target:iscsi-discover", "target:iscsi-login", "target:multipath-wait",
                "source:guest-shutdown", "source:lvm-deactivate", "target:lvm-activate", "target:check-paths",
                "hypervisor:guest-move", "target:guest-start",
                "source:multipath-flush", "source:iscsi-logout", "source:iscsi-logout",
            ],
            Kinds(plan));
        Assert.True(plan.Choices.LogoutOldIscsi);
        Assert.All(plan.Steps.Where(s => s.Kind is StepKinds.IscsiLogout or StepKinds.MultipathFlush), s => Assert.False(s.Critical));
    }

    [Fact]
    public void IscsiLogout_IsTheUsersChoice_AndKeepsLunsSomethingElseUses()
    {
        var target = new StorageStatus(
            new IscsiStatus(null, [new IscsiSession("iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260", "LOGGED_IN", []), new IscsiSession("iqn.2003-01.org.example.san:disk1", "10.0.1.5:3260", "LOGGED_IN", [])], []),
            [new MultipathMap("mpatha", "36001405aaaa", "dm-0", null, null, [])],
            null);
        var inUse = new Dictionary<(string, string), string> { [("iqn.2003-01.org.example.san:disk1", "10.0.1.5:3260")] = "vg0-db (/srv/db)" };

        var proxmoxDefault = MigrationPlanner.Plan(Input([LvmOnIscsi], target, via: "proxmox"), new MigrationOptions());
        var asked = MigrationPlanner.Plan(Input([LvmOnIscsi], target, inUse: inUse), new MigrationOptions { LogoutOldIscsi = true });
        var stay = MigrationPlanner.Plan(Input([LvmOnIscsi], target), new MigrationOptions { LogoutOldIscsi = false });

        Assert.DoesNotContain(proxmoxDefault.Steps, s => s.Kind == StepKinds.IscsiLogout);
        Assert.False(proxmoxDefault.Choices.LogoutOldIscsi);
        Assert.Equal(["iqn.2003-01.org.example.san:disk1 at 10.0.0.5:3260"], asked.Choices.IscsiToLogOut);
        Assert.Equal(["iqn.2003-01.org.example.san:disk1 at 10.0.1.5:3260 (also used by vg0-db (/srv/db))"], asked.Choices.IscsiKept);
        Assert.DoesNotContain(stay.Steps, s => s.Kind is StepKinds.IscsiLogout or StepKinds.MultipathFlush);

        // Already logged in on the new host: nothing to log in to.
        Assert.DoesNotContain(asked.Steps, s => s.Kind is StepKinds.IscsiLogin or StepKinds.IscsiDiscover or StepKinds.MultipathWait);
    }

    [Fact]
    public void DrbdDownOnTheOldHost_IsTheUsersChoice()
    {
        var plan = MigrationPlanner.Plan(Input([RawDrbd]), new MigrationOptions { DrbdDownOnOld = true });

        var down = Assert.Single(plan.Steps, s => s.Kind == StepKinds.DrbdDown);
        Assert.Equal(("source", false), (down.Side, down.Critical));
        Assert.True(plan.Choices.HasDrbd);
    }

    [Fact]
    public void DownDrbdOnTheNewHost_IsBroughtUp_AndAStaleCopyBlocks()
    {
        var up = MigrationPlanner.Plan(Input([RawDrbd], Drbd(Resource("r1", up: false))), new MigrationOptions());
        var stale = MigrationPlanner.Plan(Input([RawDrbd], Drbd(Resource("r1", disk: "Inconsistent"))), new MigrationOptions());
        var missing = MigrationPlanner.Plan(Input([RawDrbd], Drbd(Resource("r0"))), new MigrationOptions());

        Assert.Equal("target:drbd-up", Kinds(up).First());
        Assert.Contains(stale.Blockers, b => b.Contains("Inconsistent, not UpToDate", StringComparison.Ordinal));
        Assert.Contains("DRBD resource r1 isn't configured on kvm-b.", missing.Blockers);
    }

    [Fact]
    public void LocalDisks_BlockLibvirt_AndNeedCopyingOnProxmox()
    {
        var local = new GuestDisk("scsi0", "/dev/pve/vm-100-disk-0", DiskLayouts.Local, null, [new StorageLayer(LayerKinds.Lvm, "pve/vm-100-disk-0"), new StorageLayer(LayerKinds.Local, "sda")], "local-lvm");

        Assert.Contains(MigrationPlanner.Plan(Input([local]), new MigrationOptions()).Blockers, b => b.Contains("can only copy image files", StringComparison.Ordinal));
        Assert.Contains(MigrationPlanner.Plan(Input([local], via: "proxmox"), new MigrationOptions()).Blockers, b => b.Contains("copy local disks", StringComparison.Ordinal));
        var copied = MigrationPlanner.Plan(Input([local], via: "proxmox"), new MigrationOptions { WithLocalDisks = true });
        Assert.Empty(copied.Blockers);
        Assert.Equal("1", copied.Steps.Single(s => s.Kind == StepKinds.GuestMove).Args["localDisks"]);
    }

    [Fact]
    public void ALocalImageFile_IsCopied_ThroughNKeeperWhenTheHostsCantReachEachOther()
    {
        var plan = MigrationPlanner.Plan(Input([LocalFile], peer: (false, "Permission denied"), images: Image()), new MigrationOptions());

        Assert.Empty(plan.Blockers);
        Assert.Equal("offline", plan.Mode);
        Assert.Equal(["source:guest-shutdown", "target:disk-copy", "target:check-paths", "hypervisor:guest-move", "target:guest-start"], Kinds(plan));
        var copy = plan.Steps.Single(s => s.Kind == StepKinds.DiskCopy);
        Assert.Equal(
            "Copy /var/lib/libvirt/images/web.qcow2 (12 GiB) from kvm-a to kvm-b, through NKeeper, since kvm-a can't reach kvm-b over SSH (slower), then check its size there",
            copy.Title);
        Assert.Equal(("0", (12L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture)), (copy.Args["direct"], copy.Args["size"]));
        Assert.Equal("qcow2 image, 20 GiB (12 GiB used)", plan.Disks[0].Note);
        Assert.True(plan.Choices.HasLocalDisks && plan.Choices.CopyLocalDisks);
    }

    [Fact]
    public void ALocalImageFile_MovesLive_CopyingAsItGoes_WhenTheHostsReachEachOther()
    {
        var plan = MigrationPlanner.Plan(Input([LocalFile], images: Image()), new MigrationOptions { DeleteOldLocalDisks = true });

        Assert.Equal("live", plan.Mode);
        Assert.Equal(["target:disk-create", "target:check-paths", "hypervisor:guest-move", "source:disk-delete"], Kinds(plan));
        Assert.Equal("Create an empty qcow2 image of 20 GiB at /var/lib/libvirt/images/web.qcow2 on kvm-b, for the live copy (qemu-img create)", plan.Steps[0].Title);
        Assert.Equal("1", plan.Steps.Single(s => s.Kind == StepKinds.GuestMove).Args["copyStorage"]);
        Assert.Contains("--copy-storage-all", plan.Steps.Single(s => s.Kind == StepKinds.GuestMove).Title, StringComparison.Ordinal);
        Assert.False(plan.Steps.Single(s => s.Kind == StepKinds.DiskDelete).Critical);

        var noQemuImg = MigrationPlanner.Plan(Input([LocalFile], images: Image(qemuImg: false)), new MigrationOptions());
        Assert.Equal("offline", noQemuImg.Mode);
        Assert.Contains("needs qemu-img on kvm-b", noQemuImg.Choices.LiveUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalImageFile_IsntCopied_WhenThatIsntWanted_OrCantBeDoneSafely()
    {
        Assert.Contains(MigrationPlanner.Plan(Input([LocalFile], images: Image()), new MigrationOptions { WithLocalDisks = false }).Blockers, b => b.Contains("Choose to copy it to kvm-b", StringComparison.Ordinal));
        Assert.Contains(MigrationPlanner.Plan(Input([LocalFile], images: Image(backing: "base.qcow2")), new MigrationOptions()).Blockers, b => b.Contains("overlay of base.qcow2", StringComparison.Ordinal));
        Assert.Contains(MigrationPlanner.Plan(Input([LocalFile], images: Image(exists: true)), new MigrationOptions()).Blockers, b => b.Contains("already exists on kvm-b", StringComparison.Ordinal));
        Assert.Contains(MigrationPlanner.Plan(Input([LocalFile], images: Image(free: 5L << 30)), new MigrationOptions()).Blockers, b => b == "kvm-b has 5 GiB free where the disks go, but they need 12 GiB.");

        // A local device (not a file) can't be copied.
        var device = LocalFile with { Path = "/dev/sdb", Mount = null };
        Assert.Contains(MigrationPlanner.Plan(Input([device]), new MigrationOptions()).Blockers, b => b.Contains("can only copy image files", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImageFileWhoseStorageCouldntBeTraced_IsNeverCopied()
    {
        // Even with its image read: it may be on DRBD or iSCSI, which has to move instead.
        var untraced = LocalFile with { Layers = [], Note = "NKeeper couldn't tell what this is stored on." };

        var plan = MigrationPlanner.Plan(Input([untraced], images: Image()), new MigrationOptions { WithLocalDisks = true });

        Assert.Contains(plan.Blockers, b => b.Contains("couldn't tell what disk vda", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Steps, s => s.Kind is StepKinds.DiskCopy or StepKinds.DiskCreate);
        Assert.False(StorageTrace.IsLocalFile(untraced));
        Assert.False(StorageTrace.IsLocalFile(OnDrbdFilesystem));
        Assert.True(StorageTrace.IsLocalFile(LocalFile));
    }

    [Fact]
    public void StoppedVm_IsMovedWithoutShuttingDownOrStarting()
    {
        var plan = MigrationPlanner.Plan(Input([OnDrbdFilesystem], state: "shut off"), new MigrationOptions());

        Assert.DoesNotContain(plan.Steps, s => s.Kind is StepKinds.GuestShutdown or StepKinds.GuestStart);
        Assert.Equal("It isn't running.", plan.Choices.LiveUnavailableReason);
    }

    [Fact]
    public void Trace_FollowsAnImageFileThroughItsFileSystem_DownToIscsi()
    {
        var status = new StorageStatus(
            new IscsiStatus(null, [new IscsiSession("iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260", "LOGGED_IN", ["sdb"])], []),
            [new MultipathMap("mpatha", "36001405aaaa", "dm-0", null, null, [])],
            null);
        var output = string.Join('\n',
            "@@disk",
            "@@mount",
            """{"filesystems":[{"source":"/dev/mapper/vg0-vms","target":"/srv/vms","fstype":"xfs","uuid":"abcd"}]}""",
            "@@tree",
            """{"blockdevices":[{"name":"vg0-vms","kname":"dm-1","type":"lvm","tran":null,"children":[{"name":"mpatha","kname":"dm-0","type":"mpath","tran":null,"children":[{"name":"sdb","kname":"sdb","type":"disk","tran":"iscsi"}]}]}]}""",
            "@@disk",
            "@@mount",
            """{"filesystems":[{"source":"nas:/vms","target":"/mnt/nas","fstype":"nfs4","uuid":null}]}""",
            "@@lvs",
            "  /dev/mapper/vg0-vms|vg0|vms",
            "  /dev/mapper/vg0-db|vg0|db",
            "@@users sdb",
            """{"blockdevices":[{"name":"sdb","kname":"sdb","type":"disk","mountpoint":null,"children":[{"name":"mpatha","kname":"dm-0","type":"mpath","mountpoint":null,"children":[{"name":"vg0-vms","kname":"dm-1","type":"lvm","mountpoint":"/srv/vms"},{"name":"vg0-db","kname":"dm-2","type":"lvm","mountpoint":"/srv/db"}]}]}]}""");

        var result = StorageTrace.Parse(output, [new DiskPath("vda", "/srv/vms/web.qcow2"), new DiskPath("vdb", "/mnt/nas/data.raw")], status);

        var vda = result.Disks[0];
        Assert.Equal((DiskLayouts.Filesystem, "/srv/vms", "xfs"), (vda.Layout, vda.Mount!.MountPoint, vda.Mount.FsType));
        Assert.Equal(
            [new StorageLayer(LayerKinds.Lvm, "vg0/vms"), new StorageLayer(LayerKinds.Multipath, "36001405aaaa", "mpatha"), new StorageLayer(LayerKinds.Iscsi, "iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260")],
            vda.Layers);
        Assert.Equal(DiskLayouts.Network, result.Disks[1].Layout);
        Assert.Equal("vg0-db (/srv/db)", result.IscsiInUse[("iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260")]);
    }

    [Fact]
    public void Trace_KnowsAnIscsiSessionsDisk_WhenLsblkDoesntSayIscsi()
    {
        var status = new StorageStatus(
            new IscsiStatus(null, [new IscsiSession("iqn.2003-01.org.example.san:vms", "10.0.0.5:3260", "LOGGED_IN", ["sdc"])], []),
            null,
            null);
        var output = string.Join('\n',
            "@@disk",
            "@@mount",
            """{"filesystems":[{"source":"/dev/sdc1","target":"/srv/vms","fstype":"ext4","uuid":"abcd"}]}""",
            "@@tree",
            """{"blockdevices":[{"name":"sdc1","kname":"sdc1","type":"part","tran":null,"children":[{"name":"sdc","kname":"sdc","type":"disk","tran":null}]}]}""");

        var disk = Assert.Single(StorageTrace.Parse(output, [new DiskPath("vda", "/srv/vms/web.qcow2")], status).Disks);

        Assert.Equal(DiskLayouts.Filesystem, disk.Layout);
        Assert.Equal([new StorageLayer(LayerKinds.Iscsi, "iqn.2003-01.org.example.san:vms", "10.0.0.5:3260")], disk.Layers);
        Assert.False(StorageTrace.IsLocalFile(disk));
    }

    [Fact]
    public void Trace_StopsAtDrbd_WhoseBackingDiskIsEachHostsOwn()
    {
        var status = new StorageStatus(null, null, [new DrbdResource("r1", true, "Primary", [new DrbdVolume(0, 1, "UpToDate")], [])]);
        var output = string.Join('\n',
            "@@disk", "@@block", "/dev/drbd1", "@@tree",
            """{"blockdevices":[{"name":"drbd1","kname":"drbd1","type":"disk","tran":null,"children":[{"name":"vg-drbd","kname":"dm-5","type":"lvm","tran":null,"children":[{"name":"sda","kname":"sda","type":"disk","tran":"sata"}]}]}]}""");

        var disk = Assert.Single(StorageTrace.Parse(output, [new DiskPath("vda", "/dev/drbd1")], status).Disks);

        Assert.Equal(DiskLayouts.Block, disk.Layout);
        Assert.Equal([new StorageLayer(LayerKinds.Drbd, "r1", "1")], disk.Layers);
    }

    [Fact]
    public void Trace_FollowsAnInactiveLvmVolume_ThroughItsVolumeGroup()
    {
        // A stopped VM's volume has no device; its volume group's physical volume is on DRBD.
        var status = new StorageStatus(null, null, [new DrbdResource("r0", true, "Primary", [new DrbdVolume(0, 0, "UpToDate")], [])]);
        var output = string.Join('\n',
            "@@disk", "@@lv", "  drbdvg vm-100-disk-0", "@@tree",
            """{"blockdevices":[{"name":"drbd0","kname":"drbd0","type":"disk","tran":null,"children":[{"name":"sda3","kname":"sda3","type":"part","tran":null}]}]}""");

        var disk = Assert.Single(StorageTrace.Parse(output, [new DiskPath("scsi0", "/dev/drbdvg/vm-100-disk-0")], status).Disks);

        Assert.Equal(DiskLayouts.Block, disk.Layout);
        Assert.Equal([new StorageLayer(LayerKinds.Lvm, "drbdvg/vm-100-disk-0"), new StorageLayer(LayerKinds.Drbd, "r0", "0")], disk.Layers);
        Assert.Contains("vgs --noheadings -o pv_name", StorageTrace.Script([new DiskPath("scsi0", "/dev/drbdvg/vm-100-disk-0")]), StringComparison.Ordinal);
    }

    [Fact]
    public void Proxmox_NeverCopiesDisksOnDrbdOrIscsi_AndNeverSkipsOnesOnItsOwnDisk()
    {
        var lvmOnDrbd = new GuestDisk("scsi0", "/dev/drbdvg/vm-100-disk-0", DiskLayouts.Block, null, [new StorageLayer(LayerKinds.Lvm, "drbdvg/vm-100-disk-0"), new StorageLayer(LayerKinds.Drbd, "r0", "0")], "drbd-lvm");
        var thinOnIscsi = lvmOnDrbd with { Layers = [new StorageLayer(LayerKinds.Lvm, "san/vm-100-disk-0"), new StorageLayer(LayerKinds.Iscsi, "iqn.2003-01.org.example.san:disk1", "10.0.0.5:3260")], Storage = "san-thin" };
        var dirOnDrbd = OnDrbdFilesystem with { Storage = "vms" };
        var dirOnLocalDisk = LocalFile with { Storage = "local" };
        string? Check(GuestDisk disk, string type, bool shared) =>
            ProxmoxMover.SharingBlocker(new DiskPath(disk.Name, disk.Path, disk.Storage, "trace", shared, null, type), disk, "pve1", "pve2");

        Assert.Contains("LVM on drbd, which Proxmox would copy to pve2. Mark drbd-lvm as shared", Check(lvmOnDrbd, "lvm", false), StringComparison.Ordinal);
        Assert.Contains("allow-two-primaries", Check(lvmOnDrbd, "lvm", false), StringComparison.Ordinal);
        Assert.Contains("LVM-thin on iscsi: Proxmox would copy it to pve2", Check(thinOnIscsi, "lvmthin", false), StringComparison.Ordinal);
        Assert.Contains("a file system on drbd that NKeeper moves between nodes; mark vms as shared", Check(dirOnDrbd, "dir", false), StringComparison.Ordinal);
        Assert.Null(Check(dirOnDrbd, "dir", true));
        Assert.Contains("is marked shared, but disk vda (/var/lib/libvirt/images/web.qcow2) is on pve1's own disk", Check(dirOnLocalDisk, "dir", true), StringComparison.Ordinal);
        Assert.Null(Check(dirOnLocalDisk, "dir", false));
    }

    [Fact]
    public void ProxmoxSharedLvmOnDrbd_NeedsTheResourcePrimaryOnTheNewNodeToo()
    {
        var managed = new GuestDisk("scsi0", "/dev/drbdvg/vm-100-disk-0", DiskLayouts.Managed, null, [new StorageLayer(LayerKinds.Lvm, "drbdvg/vm-100-disk-0"), new StorageLayer(LayerKinds.Drbd, "r0", "0")], "drbd-lvm");

        var secondary = MigrationPlanner.Plan(Input([managed], via: "proxmox"), new MigrationOptions());
        var primary = MigrationPlanner.Plan(Input([managed], target: Drbd(Resource("r0") with { Role = "Primary" }), via: "proxmox"), new MigrationOptions());

        Assert.Contains(secondary.Blockers, b => b.Contains("which is Secondary on kvm-b", StringComparison.Ordinal) && b.Contains("allow-two-primaries", StringComparison.Ordinal));
        Assert.Empty(primary.Blockers);
        // Proxmox activates it: NKeeper hands nothing over.
        Assert.DoesNotContain(primary.Steps, s => s.Kind is StepKinds.DrbdPrimary or StepKinds.DrbdSecondary or StepKinds.DiskCopy);
    }

    [Theory]
    [InlineData("/dev/sdb", true)]
    [InlineData("/dev/dm-3", true)]
    [InlineData("/dev/mapper/mpatha", true)]
    [InlineData("/dev/disk/by-id/dm-uuid-mpath-36001405aaaa", false)]
    [InlineData("/dev/vg0/web", false)]
    public void ASharedDisk_UsedByItsKernelName_Blocks(string path, bool blocks)
    {
        var plan = MigrationPlanner.Plan(Input([LvmOnIscsi with { Path = path }], target: new StorageStatus(new IscsiStatus(null, [], []), [], null)), new MigrationOptions());

        Assert.Equal(blocks, plan.Blockers.Any(b => b.Contains("may be a different disk", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("/srv/vms/a b.qcow2", true)]
    [InlineData("/srv/vms/it's.qcow2", false)]
    [InlineData("relative/path", false)]
    public void Trace_QuotesOnlySafePaths(string path, bool safe)
    {
        if (safe)
        {
            Assert.Equal(path, StorageTrace.Quoted(path));
        }
        else
        {
            Assert.Throws<NKeeper.Api.RemoteSessions.SessionException>(() => StorageTrace.Quoted(path));
        }
    }

    [Fact]
    public void ProxmoxConfiguration_ListsTheCurrentDisks_AndStorageTypes()
    {
        var disks = ProxmoxMover.ParseDisks(
        [
            "boot: order=scsi0",
            "ide2: local:iso/debian.iso,media=cdrom,size=600M",
            "scsi0: vms-drbd:100/vm-100-disk-0.qcow2,size=32G",
            "virtio1: san-lvm:vm-100-disk-1,size=100G",
            "efidisk0: local-lvm:vm-100-disk-2,size=4M",
            "scsi3: /dev/disk/by-id/wwn-0x5000c500a1b2c3d4,size=1T",
            "unused0: vms-drbd:100/vm-100-disk-9.qcow2",
            "",
            "[before-upgrade]",
            "scsi0: vms-drbd:100/vm-100-disk-0-old.qcow2,size=32G",
        ]);
        var storages = ProxmoxMover.ParseStorageConfig(
        [
            "dir: vms-drbd", "\tpath /srv/vms", "\tcontent images", "\tshared 1", "",
            "lvm: san-lvm", "\tvgname san", "\tshared 1", "",
            "lvmthin: local-lvm", "\tthinpool data", "\tvgname pve",
        ]);

        Assert.Equal(
            ["scsi0 vms-drbd:100/vm-100-disk-0.qcow2", "virtio1 san-lvm:vm-100-disk-1", "efidisk0 local-lvm:vm-100-disk-2", "scsi3 /dev/disk/by-id/wwn-0x5000c500a1b2c3d4", "unused0 vms-drbd:100/vm-100-disk-9.qcow2"],
            disks.Select(d => $"{d.Name} {d.Volume}"));
        Assert.Equal(("dir", true), storages["vms-drbd"]);
        Assert.Equal(("lvm", true), storages["san-lvm"]);
        Assert.Equal(("lvmthin", false), storages["local-lvm"]);
    }

    [Fact]
    public void LibvirtBlockList_SkipsCdromsAndEmptyDrives()
    {
        var disks = LibvirtMover.ParseBlockList(string.Join('\n',
            " Type      Device   Target   Source",
            "-------------------------------------------------------",
            " file      disk     vda      /srv/vms/web.qcow2",
            " block     disk     vdb      /dev/drbd1",
            " file      cdrom    sda      -",
            " file      cdrom    sdb      /srv/iso/debian.iso",
            " network   disk     vdc      rbd/web-data"));

        Assert.Equal(["vda /srv/vms/web.qcow2 trace", "vdb /dev/drbd1 trace", "vdc rbd/web-data remote"], disks.Select(d => $"{d.Name} {d.Path} {d.Handling}"));
    }
}
