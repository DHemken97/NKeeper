using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Storage;

namespace NKeeper.Api.Tests;

/// <summary>Reading iscsiadm, multipath and drbdsetup output, and the commands built from it.</summary>
public sealed class StorageParsingTests
{
    [Fact]
    public void IscsiSessions_ThroughSeveralPortals_AreEachListed_WithTheirDisks()
    {
        var sessions = StorageHost.ParseIscsiSessions(
        [
            "iSCSI Transport Class version 2.0-870",
            "Target: iqn.2001-05.com.equallogic:0-8a0906-vol1 (non-flash)",
            "\tCurrent Portal: 10.0.1.5:3260,1",
            "\t\tiSCSI Session State: LOGGED_IN",
            "\t\t\tAttached scsi disk sdb\t\tState: running",
            "\t\t\tAttached scsi disk sdc\t\tState: running",
            "\tCurrent Portal: [fd00::5]:3260,1",
            "\t\tiSCSI Session State: FAILED",
            // Older iscsiadm prints no "(non-flash)".
            "Target: iqn.2003-01.org.example:disk9",
            "\tCurrent Portal: 10.0.2.5:3260,1",
            "\t\tiSCSI Session State: LOGGED_IN",
        ]);

        Assert.Equal(
            [
                ("iqn.2001-05.com.equallogic:0-8a0906-vol1", "10.0.1.5:3260", "LOGGED_IN", "sdb,sdc"),
                ("iqn.2001-05.com.equallogic:0-8a0906-vol1", "[fd00::5]:3260", "FAILED", ""),
                ("iqn.2003-01.org.example:disk9", "10.0.2.5:3260", "LOGGED_IN", ""),
            ],
            sessions.Select(s => (s.Target, s.Portal, s.State, string.Join(',', s.Disks))));
    }

    [Fact]
    public void IscsiSessions_ARedirectingSan_AreKnownByTheirPersistentPortal_AndMerged()
    {
        var sessions = StorageHost.ParseIscsiSessions(
        [
            "Target: iqn.2007-11.com.nimblestorage:vol1 (non-flash)",
            "	Current Portal: 192.168.140.141:3260,2460",
            "	Persistent Portal: 192.168.140.140:3260,2460",
            "		iSCSI Session State: LOGGED_IN",
            "			Attached scsi disk sdb		State: running",
            "	Current Portal: 192.168.140.142:3260,2460",
            "	Persistent Portal: 192.168.140.140:3260,2460",
            "		iSCSI Session State: LOGGED_IN",
            "			Attached scsi disk sdc		State: running",
        ]);

        var session = Assert.Single(sessions);
        Assert.Equal(
            ("192.168.140.140:3260", "LOGGED_IN", "sdb,sdc", "192.168.140.141:3260,192.168.140.142:3260"),
            (session.Portal, session.State, string.Join(',', session.Disks), string.Join(',', session.ConnectedTo!)));
    }

    [Fact]
    public void IscsiNodes_KeepWellFormedOnes_AndKnowWhichAreLoggedIn()
    {
        var sessions = new List<IscsiSession> { new("iqn.2003-01.org.example:a", "[fd00::5]:3260", "LOGGED_IN", []) };

        var nodes = StorageHost.ParseIscsiNodes(
        [
            "[fd00::5]:3260,1 iqn.2003-01.org.example:a",
            "10.0.0.5:3260,1 iqn.2003-01.org.example:b",
            "10.0.0.5:3260,1 iqn.2003-01.org.example:b",
            "10.0.0.5:3260,1 iqn.2003-01.org.example:c';reboot;'",
            "iscsiadm: No records found",
        ], sessions);

        Assert.Equal(
            [new IscsiNode("iqn.2003-01.org.example:a", "[fd00::5]:3260", true), new IscsiNode("iqn.2003-01.org.example:b", "10.0.0.5:3260", false)],
            nodes);
    }

    [Fact]
    public void MultipathMaps_WithAndWithoutAliases()
    {
        var maps = StorageHost.ParseMultipath(
        [
            "mpathb (3600a098038304437415d4b6a59684a52) dm-3 NETAPP,LUN C-Mode",
            "size=500G features='3 queue_if_no_path pg_init_retries 50' hwhandler='1 alua' wp=rw",
            "|-+- policy='service-time 0' prio=50 status=active",
            "| |- 1:0:0:0 sda 8:0   active ready running",
            "| `- 2:0:0:0 sdb 8:16  active ready running",
            "`-+- policy='service-time 0' prio=10 status=enabled",
            "  |- 1:0:1:0 sdc 8:32  active ghost running",
            "  `- 2:0:1:0 sdd 8:48  failed faulty offline",
            "3600a098038304437415d4b6a59684a53 dm-4 NETAPP,LUN C-Mode",
            "size=1.0T features='0' hwhandler='0' wp=rw",
        ]);

        Assert.Equal(["3600a098038304437415d4b6a59684a53", "mpathb"], maps.Select(m => m.Name));
        Assert.Equal(("3600a098038304437415d4b6a59684a53", "dm-4", "NETAPP,LUN C-Mode", "1.0T", 0), (maps[0].Wwid, maps[0].DmDevice, maps[0].Product, maps[0].Size, maps[0].Paths.Count));
        Assert.Equal("3600a098038304437415d4b6a59684a52", maps[1].Wwid);
        Assert.Equal(
            ["sda active ready running", "sdb active ready running", "sdc active ghost running", "sdd failed faulty offline"],
            maps[1].Paths.Select(p => $"{p.Device} {p.DmState} {p.PathState} {p.OnlineState}"));
    }

    [Fact]
    public void DrbdText_ForUtilitiesWithoutJson()
    {
        var resources = StorageHost.ParseDrbdText(
        [
            "r0 node-id:0 role:Primary suspended:no",
            "  volume:0 minor:0 disk:UpToDate",
            "  volume:1 minor:1 disk:UpToDate",
            "  node2 node-id:1 connection:Connected role:Secondary congested:no",
            "    volume:0 replication:SyncSource peer-disk:Inconsistent done:12.50",
            "    volume:1 replication:Established peer-disk:UpToDate",
            "  node3 node-id:2 connection:Connecting role:Unknown",
            "",
            "r1 role:Secondary",
            "  disk:Diskless",
        ]);

        Assert.Equal(["r0", "r1"], resources.Select(r => r.Name));
        Assert.Equal("Primary", resources[0].Role);
        Assert.Equal([new DrbdVolume(0, 0, "UpToDate"), new DrbdVolume(1, 1, "UpToDate")], resources[0].Volumes);
        Assert.Equal(["node2 Connected Secondary", "node3 Connecting Unknown"], resources[0].Peers.Select(p => $"{p.Name} {p.ConnectionState} {p.Role}"));
        Assert.Equal(
            [new DrbdPeerVolume(0, "SyncSource", "Inconsistent", 12.5), new DrbdPeerVolume(1, "Established", "UpToDate", null)],
            resources[0].Peers[0].Volumes);
        Assert.Equal([new DrbdVolume(0, null, "Diskless")], resources[1].Volumes);
    }

    [Fact]
    public void Status_ListsConfiguredDrbdResourcesThatAreDown_AndLeavesOutToolsThatArentInstalled()
    {
        var status = StorageHost.ParseStatus(string.Join('\n',
            "@@drbd-resources",
            "r0 r1 web-data",
            "@@drbd-json",
            """[{"name":"r1","role":"Secondary","devices":[{"volume":0,"minor":1,"disk-state":"UpToDate"}],"connections":[{"name":"b","connection-state":"Connected","peer-role":"Primary","peer-devices":[{"volume":0,"replication-state":"Established","peer-disk-state":"UpToDate","percent-in-sync":100}]}]}]"""));

        Assert.Null(status.Iscsi);
        Assert.Null(status.Multipath);
        Assert.Equal(["r0 down", "r1 Secondary", "web-data down"], status.Drbd!.Select(r => $"{r.Name} {(r.Up ? r.Role : "down")}"));
        Assert.Equal([new DrbdPeerVolume(0, "Established", "UpToDate", 100)], status.Drbd![1].Peers[0].Volumes);
    }

    [Fact]
    public void Status_WithoutRoot_SaysWhatIsNeeded()
    {
        var error = Assert.Throws<SessionException>(() => StorageHost.ParseStatus("@@noroot\n"));

        Assert.Contains("sudo without a password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_QuoteOnlyWhatTheHostReported()
    {
        var status = new StorageStatus(
            new IscsiStatus(null, [], [new IscsiNode("iqn.2003-01.org.example:a", "[fd00::5]:3260", false)]),
            [new MultipathMap("mpatha", "3600", "dm-0", null, null, [])],
            [new DrbdResource("r0", true, "Secondary", [], [])]);

        Assert.Equal(
            "$p iscsiadm -m node -T 'iqn.2003-01.org.example:a' -p '[fd00::5]:3260' --login",
            StorageHost.CommandFor(StorageActions.IscsiLogin, "iqn.2003-01.org.example:a", "[fd00::5]:3260,1", null, status).Script);
        Assert.Equal("$p iscsiadm -m discovery -t sendtargets -p 'san.example.com:3260'", StorageHost.CommandFor(StorageActions.IscsiDiscover, null, " san.example.com:3260 ", null, status).Script);
        Assert.Equal("$p multipath -f 'mpatha'", StorageHost.CommandFor(StorageActions.MultipathFlush, null, null, "mpatha", status).Script);
        Assert.Equal("$p drbdadm secondary 'r0'", StorageHost.CommandFor(StorageActions.DrbdSecondary, null, null, "r0", status).Script);

        Assert.Throws<SessionException>(() => StorageHost.CommandFor(StorageActions.MultipathFlush, null, null, "mpathz", status));
        Assert.Throws<SessionException>(() => StorageHost.CommandFor(StorageActions.IscsiRescan, null, null, null, status));
        Assert.Throws<SessionException>(() => StorageHost.CommandFor(StorageActions.DrbdUp, null, null, "r0' ; reboot", status));
    }

    [Fact]
    public void Proxmox_KnowsWhatIsOnEachMap_AndWhetherThisNodeUsesIt()
    {
        var status = StorageHost.ParseStatus(string.Join('\n',
            "@@multipath",
            "mpatha (3600a) dm-0 LIO-ORG,disk01",
            "mpathb (3600b) dm-1 LIO-ORG,disk02",
            "mpathc (3600c) dm-2 LIO-ORG,disk03",
            "mpathd (3600d) dm-3 LIO-ORG,disk04",
            "mpathe (3600e) dm-4 LIO-ORG,disk05",
            "@@pve-node",
            "pve1",
            "@@pve-nodes",
            "pve1",
            "pve2",
            "@@pve-storage",
            "dir: local",
            "\tpath /var/lib/vz",
            "",
            "lvm: san-b",
            "\tvgname vg_b",
            "\tcontent rootdir,images",
            "\tnodes pve2",
            "\tshared 1",
            "",
            "lvm: san-c",
            "\tvgname vg_c",
            "",
            "lvm: san-e",
            "\tvgname vg_e",
            "\tdisable",
            "@@lvm-pvs",
            "/dev/mapper/mpathb||vg_b",
            "/dev/mapper/3600c|/dev/dm-2|vg_c",
            "/dev/sda3|/dev/sda3|pve",
            "/dev/dm-4|/dev/dm-4|vg_e",
            "@@signatures",
            """KNAME="dm-0" FSTYPE="" PTTYPE="" """,
            """KNAME="dm-3" FSTYPE="ext4" PTTYPE="" """));

        var pve = status.Proxmox!;
        Assert.Equal(("pve1", "local,san-b,san-c,san-e", "pve1,pve2"), (pve.Node, string.Join(',', pve.AllStorage), string.Join(',', pve.ClusterNodes)));
        Assert.Equal(
            [
                new LvmMap("mpatha", null, null, null, false),
                new LvmMap("mpathb", "vg_b", null, "san-b", false),
                new LvmMap("mpathc", "vg_c", null, "san-c", true),
                new LvmMap("mpathd", null, "ext4", null, false),
                new LvmMap("mpathe", "vg_e", null, "san-e", false),
            ],
            pve.Maps);
    }

    [Fact]
    public void Proxmox_MountsAndUnmountsMaps_AndGuardsWhatUsesThem()
    {
        var pve = new ProxmoxLvm(
            "pve1", ["pve1", "pve2"], ["local", "san-b", "san-c"],
            [new ProxmoxLvmStorage("san-b", "vg_b", ["pve2"], false), new ProxmoxLvmStorage("san-c", "vg_c", [], false)],
            [new LvmMap("mpatha", null, null, null, false), new LvmMap("mpathb", "vg_b", null, "san-b", false), new LvmMap("mpathc", "vg_c", null, "san-c", true), new LvmMap("mpathd", null, "ext4", null, false)]);
        var status = new StorageStatus(
            new IscsiStatus(null, [new IscsiSession("iqn.2003-01.org.example:c", "10.0.0.5:3260", "LOGGED_IN", ["sdc"])], []),
            [new MultipathMap("mpathc", "3600c", "dm-2", null, null, [new MultipathPath("3:0:0:0", "sdc", "active", "ready", "running")])],
            null,
            pve);

        Assert.Equal("$p pvesm set 'san-b' --nodes 'pve2,pve1'", StorageHost.CommandFor(StorageActions.LvmMount, null, null, "mpathb", status).Script);
        Assert.Equal(
            "$p blkid -p '/dev/mapper/mpatha' >/dev/null 2>&1; [ $? = 2 ] || { echo '/dev/mapper/mpatha is not blank: blkid found a signature on it' >&2; exit 1; }; "
            + "$p pvcreate '/dev/mapper/mpatha' && $p vgcreate 'san-a' '/dev/mapper/mpatha' && $p pvesm add lvm 'san-a' --vgname 'san-a' --content rootdir,images --shared 1 --nodes 'pve1'",
            StorageHost.CommandFor(StorageActions.LvmMount, null, null, "mpatha", status, " san-a ").Script);
        Assert.EndsWith("$p pvesm set 'san-c' --nodes 'pve2' && $p vgchange -an 'vg_c'", StorageHost.CommandFor(StorageActions.LvmUnmount, null, null, "mpathc", status).Script);

        Assert.Contains("already a storage", Refusal(StorageActions.LvmMount, "mpatha", "SAN-B"), StringComparison.Ordinal);
        Assert.Contains("storage ID", Refusal(StorageActions.LvmMount, "mpatha", "a';reboot"), StringComparison.Ordinal);
        Assert.Contains("Name the new", Refusal(StorageActions.LvmMount, "mpatha", null), StringComparison.Ordinal);
        Assert.Contains("has ext4 on it", Refusal(StorageActions.LvmMount, "mpathd", "x1"), StringComparison.Ordinal);
        Assert.Contains("already mounted", Refusal(StorageActions.LvmMount, "mpathc", null), StringComparison.Ordinal);
        Assert.Contains("isn't mounted", Refusal(StorageActions.LvmUnmount, "mpatha", null), StringComparison.Ordinal);
        Assert.Contains("Unmount it first", Refusal(StorageActions.MultipathFlush, "mpathc", null), StringComparison.Ordinal);
        Assert.Contains("Unmount it first", Assert.Throws<SessionException>(
            () => StorageHost.CommandFor(StorageActions.IscsiLogout, "iqn.2003-01.org.example:c", "10.0.0.5:3260", null, status)).Message, StringComparison.Ordinal);

        string Refusal(string action, string name, string? storage) =>
            Assert.Throws<SessionException>(() => StorageHost.CommandFor(action, null, null, name, status, storage)).Message;
    }
}
