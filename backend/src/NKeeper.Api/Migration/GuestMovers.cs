using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NKeeper.Api.Controllers;
using NKeeper.Api.Kvm;
using NKeeper.Api.Proxmox;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Storage;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Migration;

/// <summary>
/// Moves one VM between two hosts of a hypervisor, and gives the planner what it needs: the VM's
/// state and disks, both hosts' storage, and whether the hosts reach each other over SSH. It owns
/// its connections (an SSH login to each host, and for Proxmox its API) for the migration's life.
/// </summary>
internal abstract partial class GuestMover(SshClient? sourceShell, SshClient? targetShell) : IDisposable
{
    protected static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>An SSH login to the host the VM is on (as root, or with sudo).</summary>
    public SshClient? SourceShell { get; } = sourceShell;

    /// <summary>An SSH login to the host it moves to.</summary>
    public SshClient? TargetShell { get; } = targetShell;

    public abstract string Via { get; }

    public abstract string GuestName { get; }

    public abstract string SourceName { get; }

    public abstract string TargetName { get; }

    public virtual bool IsContainer => false;

    /// <summary>"running", "stopped", "shut off"…</summary>
    public abstract Task<string> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Shuts the VM down cleanly where it is and waits until it's off.</summary>
    public abstract Task ShutdownAsync(bool onTarget, CancellationToken cancellationToken);

    public abstract Task StartAsync(bool onTarget, CancellationToken cancellationToken);

    /// <summary>Moves the VM to the new host, or (<paramref name="back"/>) back to the old one.</summary>
    /// <param name="copyStorage">libvirt live moves: copy the disks on local storage as it goes (they exist, empty, on the other host).</param>
    public abstract Task MoveAsync(bool live, bool back, bool withLocalDisks, bool copyStorage, CancellationToken cancellationToken);

    /// <summary>Copies an image file (stopped VM) from the old host to the same path on the new one, and checks it arrived whole.</summary>
    /// <param name="direct">Copy straight between the hosts over their SSH; otherwise relay it through NKeeper.</param>
    public virtual Task CopyDiskAsync(string path, long size, bool direct, CancellationToken cancellationToken) =>
        throw new SessionException(SessionErrorCodes.BadRequest, "Only libvirt migrations copy disks themselves.");

    /// <summary>libvirt: the image files on the old host's own storage, and what the new host has room for.</summary>
    protected virtual Task<IReadOnlyDictionary<string, LocalImage>?> LocalImagesAsync(IReadOnlyList<GuestDisk> disks, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, LocalImage>?>(null);

    /// <summary>Everything the planner needs, read from both hosts now.</summary>
    public async Task<PlanInput> InspectAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        var blockers = new List<string>();
        var notes = new List<string>();
        await CheckHostsAsync(blockers, notes, cancellationToken);
        if (SourceShell is null || TargetShell is null)
        {
            return new PlanInput(Via, GuestName, IsContainer, state, SourceName, TargetName, null, new Dictionary<(string, string), string>(), null, null, null, null, blockers, notes);
        }

        var source = await StorageHost.GetStatusAsync(SourceShell, cancellationToken);
        var target = await StorageHost.GetStatusAsync(TargetShell, cancellationToken);
        var paths = await GetDiskPathsAsync(cancellationToken);
        var traced = paths.Where(p => p.Handling is "trace" or "managed").ToList();
        var trace = traced.Count > 0
            ? await StorageTrace.TraceAsync(SourceShell, traced, source, cancellationToken)
            : new TraceResult([], new Dictionary<(string, string), string>());
        var disks = new List<GuestDisk>();
        foreach (var path in paths)
        {
            var found = trace.Disks.FirstOrDefault(d => d.Name == path.Name);
            disks.Add(Classify(path, found, blockers));
        }

        var peer = await PeerSshAsync(back: false, cancellationToken);
        var reverse = Via == "kvm" ? await PeerSshAsync(back: true, cancellationToken) : null;
        var images = await LocalImagesAsync(disks, cancellationToken);
        return new PlanInput(Via, GuestName, IsContainer, state, SourceName, TargetName, disks, trace.IscsiInUse, source, target, peer, reverse, blockers, notes, images);
    }

    /// <summary>Checks both hosts are what they should be (e.g. the logins reach the right nodes).</summary>
    protected abstract Task CheckHostsAsync(List<string> blockers, List<string> notes, CancellationToken cancellationToken);

    /// <summary>The VM's disks on the old host.</summary>
    protected abstract Task<IReadOnlyList<DiskPath>> GetDiskPathsAsync(CancellationToken cancellationToken);

    /// <summary>Whether one host reaches the other over SSH as root without a prompt, as migrations need.</summary>
    protected abstract Task<(bool Ok, string? Error)?> PeerSshAsync(bool back, CancellationToken cancellationToken);

    /// <summary>A disk as traced, adjusted for how the hypervisor treats its storage.</summary>
    protected virtual GuestDisk Classify(DiskPath path, GuestDisk? traced, List<string> blockers) => path.Handling switch
    {
        "managed" => new GuestDisk(path.Name, path.Path, DiskLayouts.Managed, null, traced?.Layers ?? [], path.Storage, path.Note),
        "remote" => new GuestDisk(path.Name, path.Path, DiskLayouts.Network, null, [], path.Storage, path.Note),
        "local" => new GuestDisk(path.Name, path.Path, DiskLayouts.Local, null, [], path.Storage, path.Note),
        _ => traced ?? new GuestDisk(path.Name, path.Path, DiskLayouts.Local, null, [], path.Storage, "NKeeper couldn't trace this disk."),
    };

    /// <summary>Runs a test script; ok when it exits 0, otherwise its first line of complaint.</summary>
    protected static async Task<(bool Ok, string? Error)> TestAsync(SshClient client, string script, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(script);
        command.CommandTimeout = TimeSpan.FromSeconds(30);
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            return (false, "it didn't answer in time");
        }

        var error = command.Error.Trim();
        return command.ExitStatus == 0 ? (true, null) : (false, error.Length > 0 ? error.Split('\n')[0].Trim() : $"exit code {command.ExitStatus}");
    }

    /// <summary>A host's name, as it calls itself (the short name).</summary>
    protected static async Task<string> HostNameAsync(SshClient client, CancellationToken cancellationToken) =>
        (await StorageHost.RunAsRootAsync(client, "uname -n | cut -d. -f1", CommandTimeout, "Reading the host's name", cancellationToken)).Trim();

    /// <summary>An address for SSH and libvirt URIs (IPv6 in brackets), checked to be safe in a command line.</summary>
    protected static string UriHost(string address) =>
        !AddressFormat().IsMatch(address) ? throw new SessionException(SessionErrorCodes.BadRequest, $"NKeeper can't use the address “{address}” here.")
        : address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;

    public virtual void Dispose()
    {
        SourceShell?.Dispose();
        TargetShell?.Dispose();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.:-]{0,252}$")]
    private static partial Regex AddressFormat();
}

/// <summary>A Proxmox VM or container, moved by its cluster through the API; its nodes' storage over SSH, when there are logins to them.</summary>
internal sealed partial class ProxmoxMover(
    ProxmoxController.ProxmoxConnection connection, ProxmoxGuest guest, string targetNode, SshClient? sourceShell, SshClient? targetShell)
    : GuestMover(sourceShell, targetShell)
{
    /// <summary>Storage types every node reaches over the network by itself.</summary>
    private static readonly HashSet<string> RemoteTypes = new(StringComparer.Ordinal)
    {
        "rbd", "cephfs", "nfs", "cifs", "glusterfs", "iscsidirect", "pbs", "zfs", "drbd", "linstor", "esxi",
    };

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MigrateTimeout = TimeSpan.FromHours(4);

    private readonly string _sourceNode = guest.Node;
    private ProxmoxGuest _guest = guest;

    public override string Via => "proxmox";

    public override string GuestName => _guest.Name;

    public override string SourceName => _sourceNode;

    public override string TargetName => targetNode;

    public override bool IsContainer => _guest.Type == "lxc";

    public override async Task<string> GetStateAsync(CancellationToken cancellationToken) => (await RefreshAsync(cancellationToken)).Status;

    public override async Task ShutdownAsync(bool onTarget, CancellationToken cancellationToken)
    {
        var current = await RefreshAsync(cancellationToken);
        await WaitAsync(await connection.Api.StartActionAsync(current, ProxmoxActions.Shutdown, cancellationToken), ShutdownTimeout, "Shutting it down", cancellationToken);
    }

    public override async Task StartAsync(bool onTarget, CancellationToken cancellationToken)
    {
        var current = await RefreshAsync(cancellationToken);
        await WaitAsync(await connection.Api.StartActionAsync(current, ProxmoxActions.Start, cancellationToken), ShutdownTimeout, "Starting it", cancellationToken);
    }

    public override async Task MoveAsync(bool live, bool back, bool withLocalDisks, bool copyStorage, CancellationToken cancellationToken)
    {
        var current = await RefreshAsync(cancellationToken);
        var to = back ? _sourceNode : targetNode;
        if (string.Equals(current.Node, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var upid = await connection.Api.MigrateAsync(current, to, live && current.Status == "running", withLocalDisks, cancellationToken);
        await WaitAsync(upid, MigrateTimeout, $"Moving it to {to}", cancellationToken);
    }

    protected override async Task CheckHostsAsync(List<string> blockers, List<string> notes, CancellationToken cancellationToken)
    {
        var nodes = await connection.Api.GetNodesAsync(cancellationToken);
        var target = nodes.FirstOrDefault(n => string.Equals(n.Name, targetNode, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            blockers.Add($"There's no node “{targetNode}” in this Proxmox cluster.");
        }
        else if (!target.Online)
        {
            blockers.Add($"Node {targetNode} is offline.");
        }

        if (string.Equals(targetNode, _sourceNode, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add($"“{_guest.Name}” is already on {_sourceNode}.");
        }

        foreach (var (shell, node, which) in new[] { (SourceShell, _sourceNode, "old"), (TargetShell, targetNode, "new") })
        {
            if (shell is not null && await HostNameAsync(shell, cancellationToken) is var name && !string.Equals(name, node, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add($"The SSH login for the {which} node reaches {name}, not {node}.");
            }
        }
    }

    protected override async Task<IReadOnlyList<DiskPath>> GetDiskPathsAsync(CancellationToken cancellationToken)
    {
        var folder = _guest.Type == "lxc" ? "lxc" : "qemu-server";
        var output = await StorageHost.RunAsRootAsync(
            SourceShell!,
            $"echo '@@conf'; $p cat /etc/pve/nodes/{_sourceNode}/{folder}/{_guest.VmId.ToString(CultureInfo.InvariantCulture)}.conf; echo '@@storage'; $p cat /etc/pve/storage.cfg",
            CommandTimeout,
            "Reading the VM's configuration",
            cancellationToken);
        var conf = Section(output, "conf");
        var storages = ParseStorageConfig(Section(output, "storage"));
        var volumes = ParseDisks(conf);
        (string Type, bool Shared)? Lookup(string id) => storages.TryGetValue(id, out var found) ? found : null;

        // Paths of the volumes on storage that's traced (pvesm path; passthrough devices and bind mounts are paths already).
        var toResolve = volumes.Where(v => !v.Volume.StartsWith('/') && Handling(Lookup(v.Volume.Split(':')[0])) is "trace" or "managed").ToList();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (toResolve.Count > 0)
        {
            var script = new StringBuilder();
            foreach (var volume in toResolve)
            {
                script.Append(CultureInfo.InvariantCulture, $"echo '@@{volume.Volume}'; $p pvesm path '{volume.Volume}' 2>/dev/null; ");
            }

            var resolved = await StorageHost.RunAsRootAsync(SourceShell!, script + "exit 0", CommandTimeout, "Finding the VM's disks", cancellationToken);
            foreach (var volume in toResolve)
            {
                if (Section(resolved, volume.Volume).FirstOrDefault(l => l.StartsWith('/')) is { } path)
                {
                    paths[volume.Volume] = path.Trim();
                }
            }
        }

        return [.. volumes.Select(v =>
        {
            if (v.Volume.StartsWith('/'))
            {
                return new DiskPath(v.Name, v.Volume);
            }

            var id = v.Volume.Split(':')[0];
            var storage = Lookup(id);
            var handling = Handling(storage);
            var note = storage is null ? $"Storage {id} isn't in storage.cfg." : $"{storage.Value.Type} storage {id}";
            return handling is "trace" or "managed" && !paths.ContainsKey(v.Volume)
                ? new DiskPath(v.Name, v.Volume, id, "local", false, $"Couldn't find where {v.Volume} is.")
                : new DiskPath(v.Name, paths.GetValueOrDefault(v.Volume) ?? v.Volume, id, handling, storage?.Shared ?? false, note);
        })];
    }

    protected override GuestDisk Classify(DiskPath path, GuestDisk? traced, List<string> blockers)
    {
        var disk = base.Classify(path, traced, blockers);
        if (disk.Layout == DiskLayouts.Filesystem && path.Storage is { } storage && !path.Shared)
        {
            blockers.Add($"Disk {disk.Name} is on storage {storage}, a file system on {string.Join(" / ", disk.Layers.Select(l => l.Kind))} that NKeeper moves between nodes; mark {storage} as shared (Datacenter ▸ Storage) so Proxmox doesn't copy it.");
        }

        return disk with { Note = disk.Note ?? path.Note };
    }

    protected override async Task<(bool Ok, string? Error)?> PeerSshAsync(bool back, CancellationToken cancellationToken)
    {
        if (SourceShell is null || back)
        {
            return null;
        }

        try
        {
            return await TestAsync(SourceShell, await ProxmoxHost.NodeSshCommandAsync(SourceShell, targetNode, "true", "where it moves to", cancellationToken), cancellationToken);
        }
        catch (SessionException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>How a storage type moves: Proxmox activates iSCSI and shared LVM itself, network storage is everywhere, other non-shared block storage is local.</summary>
    private static string Handling((string Type, bool Shared)? storage) => storage switch
    {
        null => "local",
        { Type: var type } when RemoteTypes.Contains(type) => "remote",
        { Type: "iscsi" } or { Type: "lvm", Shared: true } => "managed",
        { Type: "dir" or "btrfs" } => "trace",
        _ => "local",
    };

    /// <summary>The disks in a VM's or container's current configuration (before its snapshots), without CD-ROMs.</summary>
    internal static List<(string Name, string Volume)> ParseDisks(IEnumerable<string> conf)
    {
        var disks = new List<(string, string)>();
        foreach (var line in conf)
        {
            if (line.StartsWith('['))
            {
                break;
            }

            var match = DiskLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            var volume = value.Split(',')[0];
            if (value.Contains("media=cdrom", StringComparison.Ordinal) || volume is "none" or "cdrom" || !VolumeFormat().IsMatch(volume))
            {
                continue;
            }

            disks.Add((match.Groups["key"].Value, volume));
        }

        return disks;
    }

    /// <summary><c>storage.cfg</c>: each storage's type and whether it is marked shared.</summary>
    internal static Dictionary<string, (string Type, bool Shared)> ParseStorageConfig(IEnumerable<string> lines)
    {
        var storages = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        string? id = null;
        foreach (var line in lines)
        {
            if (StorageHeader().Match(line) is { Success: true } header)
            {
                id = header.Groups["id"].Value;
                storages[id] = (header.Groups["type"].Value, false);
            }
            else if (id is not null && line.Trim() is "shared 1")
            {
                storages[id] = (storages[id].Item1, true);
            }
        }

        return storages;
    }

    private async Task<ProxmoxGuest> RefreshAsync(CancellationToken cancellationToken) =>
        _guest = await connection.Api.FindGuestAsync(_guest.VmId, cancellationToken)
            ?? throw new SessionException(SessionErrorCodes.HostNotFound, $"Proxmox no longer lists VM {_guest.VmId}.");

    private async Task WaitAsync(string upid, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var status = await connection.Api.GetTaskStatusAsync(upid, cancellationToken);
            if (!status.Running)
            {
                if (status.ExitStatus != "OK")
                {
                    throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{what} failed in Proxmox: {status.ExitStatus ?? "no reason given"}.");
                }

                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new SessionException(SessionErrorCodes.Timeout, $"{what} didn't finish in time; it may still be running in Proxmox.");
            }
        }
    }

    private static List<string> Section(string output, string name)
    {
        var lines = new List<string>();
        var inside = false;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inside = line == $"@@{name}";
            }
            else if (inside)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    public override void Dispose()
    {
        connection.Dispose();
        base.Dispose();
    }

    [GeneratedRegex(@"^(?<key>(?:ide|sata|scsi|virtio)\d+|efidisk0|tpmstate0|unused\d+|rootfs|mp\d+):\s*(?<value>.+)$")]
    private static partial Regex DiskLine();

    /// <summary>A volume ID (<c>storage:volume</c>) or an absolute path, safe in single quotes.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9._-]*:[A-Za-z0-9._/+=-]+|/[^'\\\x00-\x1f]*)$")]
    private static partial Regex VolumeFormat();

    [GeneratedRegex(@"^(?<type>[a-z0-9]+):\s*(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)\s*$")]
    private static partial Regex StorageHeader();
}

/// <summary>
/// A libvirt VM moved between two KVM hosts NKeeper logs in to over SSH: offline by copying its
/// definition across (<c>dumpxml</c>, <c>define</c>, <c>undefine</c>), or live with
/// <c>virsh migrate</c> over the hosts' own SSH between each other.
/// </summary>
internal sealed partial class LibvirtMover(Guid vm, string vmName, SshClient sourceShell, SshClient targetShell, string sourceName, string targetName, string sourceAddress, string targetAddress, bool sameHost)
    : GuestMover(sourceShell, targetShell)
{
    private const string Virsh = "$p virsh -c qemu:///system";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MigrateTimeout = TimeSpan.FromHours(4);

    /// <summary>The VM's definition, kept from an offline move to put back if it's rolled back.</summary>
    private string? _xml;

    public override string Via => "kvm";

    public override string GuestName => vmName;

    public override string SourceName => sourceName;

    public override string TargetName => targetName;

    private string Id => vm.ToString("D");

    public override async Task<string> GetStateAsync(CancellationToken cancellationToken) =>
        (await Run(SourceShell!, $"{Virsh} domstate {Id}", "Reading the VM's state", cancellationToken)).Trim().Split('\n')[0].Trim();

    public override async Task ShutdownAsync(bool onTarget, CancellationToken cancellationToken)
    {
        var shell = onTarget ? TargetShell! : SourceShell!;
        await Run(shell, $"{Virsh} shutdown {Id}", "Shutting it down", cancellationToken);
        var deadline = DateTimeOffset.UtcNow + ShutdownTimeout;
        while ((await Run(shell, $"{Virsh} domstate {Id}", "Reading the VM's state", cancellationToken)).Trim() != "shut off")
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new SessionException(SessionErrorCodes.Timeout, $"“{vmName}” didn't shut down within {ShutdownTimeout.TotalMinutes:0} minutes. Check that it handles ACPI (or has the guest agent).");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    public override Task StartAsync(bool onTarget, CancellationToken cancellationToken) =>
        Run(onTarget ? TargetShell! : SourceShell!, $"{Virsh} start {Id}", "Starting it", cancellationToken);

    public override async Task MoveAsync(bool live, bool back, bool withLocalDisks, bool copyStorage, CancellationToken cancellationToken)
    {
        var (from, to, toAddress) = back ? (TargetShell!, SourceShell!, sourceAddress) : (SourceShell!, TargetShell!, targetAddress);
        if (live)
        {
            await Run(
                from,
                $"{Virsh} migrate --live --persistent --undefinesource{(copyStorage ? " --copy-storage-all" : "")} --verbose {Id} 'qemu+ssh://root@{UriHost(toAddress)}/system'",
                $"Moving it to {(back ? sourceName : targetName)}",
                cancellationToken,
                MigrateTimeout);
            return;
        }

        // Offline: define it there from this host's definition, then remove it here (undoing the definition if that fails).
        var xml = back && _xml is not null ? _xml : await Run(from, $"{Virsh} dumpxml --migratable {Id}", "Reading the VM's definition", cancellationToken);
        _xml ??= xml;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        await Run(to, $"f=$(mktemp) && echo '{encoded}' | base64 -d > \"$f\" && {Virsh} define \"$f\"; r=$?; rm -f \"$f\"; exit $r", "Defining it on the new host", cancellationToken);
        try
        {
            await Run(from, $"{Virsh} undefine --keep-nvram {Id} 2>/dev/null || {Virsh} undefine {Id}", "Removing it from the old host", cancellationToken);
        }
        catch
        {
            await Run(to, $"{Virsh} undefine --keep-nvram {Id} 2>/dev/null || {Virsh} undefine {Id}", "Removing the copy again", CancellationToken.None);
            throw;
        }
    }

    protected override async Task CheckHostsAsync(List<string> blockers, List<string> notes, CancellationToken cancellationToken)
    {
        if (sameHost)
        {
            blockers.Add($"“{vmName}” is already on {targetName}.");
            return;
        }

        var info = await Run(SourceShell!, $"{Virsh} dominfo {Id}", "Reading the VM", cancellationToken);
        if (info.Split('\n').Any(l => l.StartsWith("Managed save:", StringComparison.Ordinal) && l.Contains("yes", StringComparison.Ordinal)))
        {
            blockers.Add($"“{vmName}” is hibernated (it has a managed save). Start it, or discard the saved state, first.");
        }

        var state = await GetStateAsync(cancellationToken);
        if (state is not ("running" or "shut off"))
        {
            blockers.Add($"“{vmName}” is {state}. Resume or stop it first.");
        }

        var there = await TestAsync(TargetShell!, $"PATH=$PATH:/usr/sbin:/sbin; p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; {Virsh} domstate {Id}", cancellationToken);
        if (there.Ok)
        {
            blockers.Add($"{targetName} already has a VM with this UUID defined.");
        }

        var libvirt = await TestAsync(TargetShell!, $"PATH=$PATH:/usr/sbin:/sbin; p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; {Virsh} list --all --name >/dev/null", cancellationToken);
        if (!libvirt.Ok)
        {
            blockers.Add($"libvirt on {targetName} didn't answer: {libvirt.Error}");
        }
    }

    protected override async Task<IReadOnlyList<DiskPath>> GetDiskPathsAsync(CancellationToken cancellationToken)
    {
        var output = await Run(SourceShell!, $"{Virsh} domblklist --details {Id}", "Reading the VM's disks", cancellationToken);
        return ParseBlockList(output);
    }

    protected override async Task<(bool Ok, string? Error)?> PeerSshAsync(bool back, CancellationToken cancellationToken)
    {
        var (from, address) = back ? (TargetShell!, sourceAddress) : (SourceShell!, targetAddress);
        try
        {
            return await TestAsync(
                from,
                $"PATH=$PATH:/usr/sbin:/sbin; p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; $p ssh -o BatchMode=yes -o ConnectTimeout=8 root@{UriHost(address).Trim('[', ']')} true",
                cancellationToken);
        }
        catch (SessionException ex)
        {
            return (false, ex.Message);
        }
    }

    protected override async Task<IReadOnlyDictionary<string, LocalImage>?> LocalImagesAsync(IReadOnlyList<GuestDisk> disks, CancellationToken cancellationToken)
    {
        // Files (not devices) on the old host's own storage.
        var files = disks.Where(d => d.Layout == DiskLayouts.Local && d.Mount is not null).Select(d => StorageTrace.Quoted(d.Path)).Distinct().ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var source = new StringBuilder();
        var target = new StringBuilder("command -v qemu-img >/dev/null && echo '@@qemu-img'; ");
        foreach (var file in files)
        {
            source.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"echo '@@image {file}'; $p qemu-img info -U --output=json '{file}' 2>/dev/null || $p qemu-img info --output=json '{file}' 2>/dev/null; echo '@@stat {file}'; $p stat -c '%s %b %B' '{file}'; ");
            target.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"echo '@@target {file}'; [ -e '{file}' ] && echo exists; d=$(dirname '{file}'); while [ ! -d \"$d\" ]; do d=$(dirname \"$d\"); done; df -Pk \"$d\" | awk 'NR == 2 {{ printf \"%.0f\\n\", $4 * 1024 }}'; ");
        }

        var info = Sections(await Run(SourceShell!, source + "exit 0", "Reading the VM's local disks", cancellationToken));
        var there = Sections(await Run(TargetShell!, target + "exit 0", "Checking room for the disks on the new host", cancellationToken));
        var images = new Dictionary<string, LocalImage>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var stat = (info.GetValueOrDefault($"stat {file}") ?? []).FirstOrDefault()?.Split(' ');
            if (stat is not [var length, var blocks, var blockSize]
                || !long.TryParse(length, out var fileSize) || !long.TryParse(blocks, out var blockCount) || !long.TryParse(blockSize, out var unit))
            {
                continue;
            }

            string format = "raw";
            long virtualSize = fileSize, allocated = blockCount * unit;
            string? backing = null;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(string.Join('\n', info.GetValueOrDefault($"image {file}") ?? []));
                var root = json.RootElement;
                format = root.TryGetProperty("format", out var f) && f.GetString() is { Length: > 0 } name ? name : format;
                virtualSize = root.TryGetProperty("virtual-size", out var v) && v.TryGetInt64(out var size) ? size : virtualSize;
                backing = root.TryGetProperty("backing-filename", out var b) ? b.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // No qemu-img on the old host: a raw file, as big as it is.
            }

            var targetLines = there.GetValueOrDefault($"target {file}") ?? [];
            images[file] = new LocalImage(
                format,
                virtualSize,
                allocated,
                fileSize,
                backing,
                targetLines.Contains("exists"),
                targetLines.Select(l => long.TryParse(l, out var free) ? free : (long?)null).LastOrDefault(l => l is not null),
                there.ContainsKey("qemu-img"));
        }

        return images;
    }

    public override async Task CopyDiskAsync(string path, long size, bool direct, CancellationToken cancellationToken)
    {
        var file = StorageTrace.Quoted(path);
        var hours = TimeSpan.FromHours(24);
        await Run(TargetShell!, $"[ -e '{file}' ] && {{ echo '{file} already exists on this host' >&2; exit 1; }}; $p mkdir -p \"$(dirname '{file}')\"", "Preparing the copy", cancellationToken);
        if (direct)
        {
            // rsync keeps the file sparse; without rsync on both hosts, dd over ssh (sparse where dd can).
            var host = UriHost(targetAddress);
            await Run(
                SourceShell!,
                $"a='{host.Trim('[', ']')}'; "
                + $"if command -v rsync >/dev/null && $p ssh -o BatchMode=yes \"root@$a\" 'command -v rsync >/dev/null'; then "
                + $"$p rsync -aS -e 'ssh -o BatchMode=yes' '{file}' 'root@{host}:{file}'; "
                + $"else $p dd if='{file}' bs=4M 2>/dev/null | $p ssh -o BatchMode=yes \"root@$a\" \"dd of='{file}' bs=4M conv=sparse 2>/dev/null || dd of='{file}' bs=4M\"; fi",
                $"Copying {path}",
                cancellationToken,
                hours);
        }
        else
        {
            await RelayAsync(file, size, cancellationToken);
        }

        var copied = (await Run(TargetShell!, $"$p stat -c %s '{file}'", "Checking the copy", cancellationToken)).Trim();
        if (copied != size.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The copy of {path} on {targetName} is {copied} bytes, not {size}.");
        }
    }

    /// <summary>
    /// Copies a file through NKeeper's own SSH connections, in 64 MiB pieces read with dd on the old
    /// host and written to one dd on the new host, so memory use stays small whatever its size.
    /// </summary>
    private async Task RelayAsync(string file, long size, CancellationToken cancellationToken)
    {
        const int PieceMiB = 64;
        using var sink = TargetShell!.CreateCommand(
            StorageHost.AsRoot + $"o=''; dd --help 2>&1 | grep -q sparse && o=conv=sparse; exec $p dd of='{file}' bs=1M $o 2>/dev/null");
        sink.CommandTimeout = TimeSpan.FromHours(24);
        var writing = sink.ExecuteAsync(cancellationToken);
        await using (var input = sink.CreateInputStream())
        {
            for (long piece = 0; piece * PieceMiB * 1024L * 1024L < size; piece++)
            {
                using var read = SourceShell!.CreateCommand(StorageHost.AsRoot + $"$p dd if='{file}' bs=1M skip={piece * PieceMiB} count={PieceMiB} 2>/dev/null");
                read.CommandTimeout = TimeSpan.FromMinutes(10);
                await read.ExecuteAsync(cancellationToken);
                if (read.ExitStatus is not 0)
                {
                    throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Reading {file} on {sourceName} failed: {read.Error.Trim()}");
                }

                await read.OutputStream.CopyToAsync(input, cancellationToken);
            }
        }

        await writing;
        if (sink.ExitStatus is not 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Writing {file} on {targetName} failed: {sink.Error.Trim()}");
        }
    }

    private static Dictionary<string, List<string>> Sections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..]] = [];
            }
            else
            {
                current?.Add(line.Trim());
            }
        }

        return sections;
    }

    /// <summary><c>virsh domblklist --details</c>: type, device, target and source per disk; CD-ROMs, floppies and empty drives are left out.</summary>
    internal static List<DiskPath> ParseBlockList(string output)
    {
        var disks = new List<DiskPath>();
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            var fields = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] is not ("file" or "block" or "network" or "volume" or "dir") || fields[1] is not ("disk" or "lun"))
            {
                continue;
            }

            var (type, target, source) = (fields[0], fields[2], fields[3].Trim());
            if (source == "-")
            {
                continue;
            }

            disks.Add(type is "file" or "block" && source.StartsWith('/')
                ? new DiskPath(target, source)
                : new DiskPath(target, source, null, "remote", false, $"A {type} disk libvirt reaches by itself; NKeeper doesn't move it."));
        }

        return disks;
    }

    private static Task<string> Run(SshClient client, string script, string what, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        StorageHost.RunAsRootAsync(client, script, timeout ?? CommandTimeout, what, cancellationToken);
}
