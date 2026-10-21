using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Storage;
using Renci.SshNet;

namespace NKeeper.Api.Migration;

/// <summary>A step as the browser shows it: "pending", "running", "done", "skipped", "warning", "failed", "undoing", "undone" or "undoFailed".</summary>
public sealed record MigrationStepView(string Title, string Side, string State, string? Message, bool Critical);

/// <summary>
/// A migration's progress: "running", then "succeeded" (perhaps with warnings), "rolledBack"
/// (a step failed and everything was put back) or "rollbackFailed" (putting it back failed too:
/// <see cref="Message"/> says what to fix by hand).
/// </summary>
public sealed record MigrationJobView(string Id, string Guest, string Source, string Target, string Mode, string State, IReadOnlyList<MigrationStepView> Steps, string? Message);

/// <summary>Migrations running on the server (they go on if the browser closes), each visible only to the user who started it.</summary>
public sealed class MigrationJobs(AuditLog audit, ILogger<MigrationJobs> logger)
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, MigrationJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Starts running the plan; null when the same VM is already being moved.</summary>
    internal MigrationJob? Start(Guid userId, Guid hostId, string vmKey, GuestMover mover, MigrationPlan plan)
    {
        foreach (var old in _jobs.Values.Where(j => j.Finished is { } f && DateTimeOffset.UtcNow - f > KeepFinished))
        {
            _jobs.TryRemove(old.Id, out _);
        }

        if (_jobs.Values.Any(j => j.VmKey == vmKey && j.Finished is null))
        {
            return null;
        }

        var job = new MigrationJob(userId, hostId, vmKey, mover, plan, audit, logger);
        _jobs[job.Id] = job;
        // The job outlives the request: it mustn't carry the request's context (its HttpContext) along.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(job.RunAsync);
        }

        return job;
    }

    internal MigrationJob? Find(string id, Guid userId) => _jobs.TryGetValue(id, out var job) && job.UserId == userId ? job : null;
}

/// <summary>
/// Runs a plan's steps in order. When a critical step fails, every step already done that changed
/// something is undone, newest first. Steps that tidy up the old host only warn when they fail.
/// </summary>
internal sealed partial class MigrationJob(
    Guid userId, Guid hostId, string vmKey, GuestMover mover, MigrationPlan plan, AuditLog audit, ILogger logger)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3);

    private readonly object _lock = new();
    private readonly List<(PlannedStep Step, string State, string? Message)> _steps = [.. plan.Steps.Select(s => (s, "pending", (string?)null))];
    private string _state = "running";
    private string? _message;

    public string Id { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public Guid UserId { get; } = userId;

    public string VmKey { get; } = vmKey;

    public DateTimeOffset? Finished { get; private set; }

    public MigrationJobView View()
    {
        lock (_lock)
        {
            return new MigrationJobView(
                Id, mover.GuestName, plan.SourceName, plan.TargetName, plan.Mode, _state,
                [.. _steps.Select(s => new MigrationStepView(s.Step.Title, s.Step.Side, s.State, s.Message, s.Step.Critical))], _message);
        }
    }

    public async Task RunAsync()
    {
        var summary = $"{mover.GuestName} · {plan.SourceName} → {plan.TargetName} ({plan.Mode})";
        await audit.RecordAsync(new AuditEntry("migration.start", hostId, summary) { UserId = UserId });
        var changed = new List<int>();
        var warnings = 0;
        var failedFlushes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (step.Kind == StepKinds.IscsiLogout && plan.Steps.Any(s => s.Kind == StepKinds.MultipathFlush && failedFlushes.Contains(s.Args["wwid"]) && Uses(step, s)))
                {
                    Set(i, "skipped", "Its multipath map is still in use, so the old host stays logged in.");
                    continue;
                }

                Set(i, "running", null);
                try
                {
                    var outcome = await DoAsync(step, undo: false);
                    if (outcome.Changed)
                    {
                        changed.Add(i);
                    }

                    Set(i, outcome.Changed ? "done" : "skipped", outcome.Message);
                }
                catch (Exception ex) when (ex is SessionException or Renci.SshNet.Common.SshException or HttpRequestException or InvalidOperationException or OperationCanceledException)
                {
                    var message = ex is SessionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                    if (!step.Critical)
                    {
                        warnings++;
                        if (step.Kind == StepKinds.MultipathFlush)
                        {
                            failedFlushes.Add(step.Args["wwid"]);
                        }

                        Set(i, "warning", message);
                        continue;
                    }

                    Set(i, "failed", message);
                    await RollBackAsync(changed, message);
                    return;
                }
            }

            Finish("succeeded", warnings > 0 ? $"Moved to {plan.TargetName}, but {warnings} {(warnings == 1 ? "step" : "steps")} tidying up {plan.SourceName} didn't work; see below." : $"Moved to {plan.TargetName}.");
        }
#pragma warning disable CA1031 // A migration must always end with a state the user can see.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Migration {Id} of {Guest} failed unexpectedly", Id, mover.GuestName);
            Finish("rollbackFailed", $"NKeeper stopped unexpectedly ({ex.GetType().Name}). Check {mover.GuestName}'s storage on both hosts by hand.");
        }
        finally
        {
            mover.Dispose();
            var view = View();
            await audit.RecordAsync(new AuditEntry("migration.end", hostId, $"{summary} · {view.State}{(view.Message is null ? "" : $" · {view.Message}")}") { UserId = UserId });
        }
    }

    /// <summary>Whether a logout step is of the target a multipath flush step's map is built on.</summary>
    private bool Uses(PlannedStep logout, PlannedStep flush) =>
        plan.Disks.Any(d => d.Layers.Any(l => l.Kind == LayerKinds.Multipath && l.Name == flush.Args["wwid"])
            && d.Layers.Any(l => l.Kind == LayerKinds.Iscsi && l.Name == logout.Args["target"] && l.Detail == logout.Args["portal"]));

    private async Task RollBackAsync(List<int> changed, string reason)
    {
        var failures = new List<string>();
        foreach (var i in Enumerable.Reverse(changed))
        {
            var step = plan.Steps[i];
            Set(i, "undoing", null);
            try
            {
                await DoAsync(step, undo: true);
                Set(i, "undone", null);
            }
#pragma warning disable CA1031 // Keep putting back the rest; the user is told what's left.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var message = ex is SessionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                Set(i, "undoFailed", message);
                failures.Add($"{step.Title}: {message}");
            }
        }

        Finish(
            failures.Count == 0 ? "rolledBack" : "rollbackFailed",
            failures.Count == 0
                ? $"{reason} Everything was put back as it was{(changed.Count == 0 ? " (nothing had changed yet)" : "")}."
                : $"{reason} Putting it back failed too, so fix by hand: {string.Join(" · ", failures)}");
    }

    private void Set(int index, string state, string? message)
    {
        lock (_lock)
        {
            _steps[index] = (_steps[index].Step, state, message);
        }
    }

    private void Finish(string state, string message)
    {
        lock (_lock)
        {
            _state = state;
            _message = message;
            Finished = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Runs a step (or its undo); says whether it changed anything (an undo only runs for one that did).</summary>
    private async Task<(bool Changed, string? Message)> DoAsync(PlannedStep step, bool undo)
    {
        var ct = CancellationToken.None;
        var a = step.Args;
        string Arg(string key) => Safe(a[key]);
        string Path(string key) => StorageTrace.Quoted(a[key]);

        switch (step.Kind, undo)
        {
            case (StepKinds.GuestShutdown, false):
                await mover.ShutdownAsync(onTarget: false, ct);
                return (true, null);
            case (StepKinds.GuestShutdown, true):
                await mover.StartAsync(onTarget: false, ct);
                return (true, null);
            case (StepKinds.GuestStart, false):
                await mover.StartAsync(onTarget: true, ct);
                return (true, null);
            case (StepKinds.GuestStart, true):
                await mover.ShutdownAsync(onTarget: true, ct);
                return (true, null);
            case (StepKinds.GuestMove, _):
                await mover.MoveAsync(a["live"] == "1", back: undo, a["localDisks"] == "1", ct);
                return (true, null);
        }

        var target = step.Side == "target";
        var shell = (target ? mover.TargetShell : mover.SourceShell)
            ?? throw new SessionException(SessionErrorCodes.BadRequest, "There's no SSH login to that host.");
        var other = target ? mover.SourceShell! : mover.TargetShell!;
        var script = (step.Kind, undo) switch
        {
            (StepKinds.IscsiDiscover, false) => $"$p iscsiadm -m discovery -t sendtargets -p '{Arg("portal")}' >/dev/null; echo same",
            (StepKinds.IscsiLogin, false) or (StepKinds.IscsiLogout, true) => IscsiSession(Arg("target"), Arg("portal"))
                + "&& { echo same; exit 0; }; "
                + $"$p iscsiadm -m node -T '{Arg("target")}' -p '{Arg("portal")}' --login >/dev/null && {{ command -v udevadm >/dev/null && $p udevadm settle; }}; "
                + IscsiSession(Arg("target"), Arg("portal")) + $"&& echo changed || {{ echo 'iscsiadm reported success, but there is no session to {Arg("target")} at {Arg("portal")}' >&2; exit 1; }}",
            (StepKinds.IscsiLogin, true) => $"$p iscsiadm -m node -T '{Arg("target")}' -p '{Arg("portal")}' --logout >/dev/null",
            (StepKinds.IscsiLogout, false) => IscsiSession(Arg("target"), Arg("portal")) + "|| { echo same; exit 0; }; "
                + IscsiUnused(Arg("target"), Arg("portal"))
                + $"$p iscsiadm -m node -T '{Arg("target")}' -p '{Arg("portal")}' --logout >/dev/null && echo changed",
            (StepKinds.MultipathWait, false) => $"$p multipath -r >/dev/null 2>&1; for i in $(seq 30); do [ -e /dev/disk/by-id/dm-uuid-mpath-{Arg("wwid")} ] && {{ echo same; exit 0; }}; sleep 1; done; "
                + $"echo 'no multipath map for {Arg("wwid")} appeared' >&2; exit 1",
            (StepKinds.MultipathFlush, false) => $"[ -e /dev/disk/by-id/dm-uuid-mpath-{Arg("wwid")} ] || {{ echo same; exit 0; }}; $p multipath -f '{Arg("map")}' && echo changed",
            (StepKinds.MultipathFlush, true) => "$p multipath -r >/dev/null",
            (StepKinds.DrbdUp, false) or (StepKinds.DrbdDown, true) => $"$p drbdsetup status '{Arg("resource")}' >/dev/null 2>&1 && {{ echo same; exit 0; }}; $p drbdadm up '{Arg("resource")}' && echo changed",
            (StepKinds.DrbdDown, false) or (StepKinds.DrbdUp, true) => $"$p drbdsetup status '{Arg("resource")}' >/dev/null 2>&1 || {{ echo same; exit 0; }}; $p drbdadm down '{Arg("resource")}' && echo changed",
            (StepKinds.DrbdPrimary, false) or (StepKinds.DrbdSecondary, true) => Role(Arg("resource"), "Primary"),
            (StepKinds.DrbdSecondary, false) or (StepKinds.DrbdPrimary, true) => Role(Arg("resource"), "Secondary"),
            (StepKinds.DrbdReady, false) => $"for i in $(seq 120); do d=$($p drbdadm dstate '{Arg("resource")}' 2>/dev/null | head -n 1); c=$($p drbdadm cstate '{Arg("resource")}' 2>/dev/null | head -n 1); "
                + "case \"$d\" in UpToDate*) case \"$c\" in *Connected*) echo same; exit 0;; esac;; esac; sleep 1; done; "
                + $"echo \"{Arg("resource")} is $d and $c, not UpToDate and connected\" >&2; exit 1",
            (StepKinds.DrbdAllowTwoPrimaries, false) or (StepKinds.DrbdDisallowTwoPrimaries, true) => $"$p drbdadm net-options --allow-two-primaries=yes '{Arg("resource")}' && echo changed",
            (StepKinds.DrbdDisallowTwoPrimaries, false) or (StepKinds.DrbdAllowTwoPrimaries, true) => $"$p drbdadm net-options --allow-two-primaries=no '{Arg("resource")}' && echo changed",
            (StepKinds.LvmActivate, false) or (StepKinds.LvmDeactivate, true) => $"$p vgscan --mknodes >/dev/null 2>&1; [ \"$($p lvs --noheadings -o lv_active '{Arg("lv")}' 2>/dev/null | tr -d ' ')\" = active ] && {{ echo same; exit 0; }}; "
                + $"$p lvchange -ay '{Arg("lv")}' && echo changed",
            (StepKinds.LvmDeactivate, false) or (StepKinds.LvmActivate, true) => $"[ \"$($p lvs --noheadings -o lv_active '{Arg("lv")}' 2>/dev/null | tr -d ' ')\" = active ] || {{ echo same; exit 0; }}; "
                + $"$p lvchange -an '{Arg("lv")}' && echo changed",
            (StepKinds.Mount, false) or (StepKinds.Unmount, true) => MountScript(a),
            (StepKinds.Unmount, false) or (StepKinds.Mount, true) or (StepKinds.EnsureMounted, true) => $"mountpoint -q '{Path("mountpoint")}' || {{ echo same; exit 0; }}; $p umount '{Path("mountpoint")}' && echo changed",
            (StepKinds.EnsureMounted, false) => $"mountpoint -q '{Path("mountpoint")}' && {{ echo same; exit 0; }}; $p mount '{Path("mountpoint")}' && echo changed",
            (StepKinds.CheckPaths, false) => CheckPaths(a["paths"]),
            (StepKinds.IscsiDiscover or StepKinds.MultipathWait or StepKinds.DrbdReady or StepKinds.CheckPaths, true) => "echo same",
            _ => throw new InvalidOperationException($"Unknown step {step.Kind}."),
        };

        var what = undo ? $"Undoing “{step.Title}”" : step.Title;
        if (step.Side == "both")
        {
            var first = await StorageHost.RunAsRootAsync(shell, script, StepTimeout, what, ct);
            var second = await StorageHost.RunAsRootAsync(other, script, StepTimeout, what, ct);
            return (first.Contains("changed", StringComparison.Ordinal) || second.Contains("changed", StringComparison.Ordinal), null);
        }

        var output = await StorageHost.RunAsRootAsync(shell, script, StepTimeout, what, ct);
        return (!output.Contains("same", StringComparison.Ordinal), output.Contains("in use", StringComparison.Ordinal) ? output.Trim() : null);
    }

    /// <summary>
    /// Succeeds when the host has a session to the target logged in through the portal (its persistent
    /// portal: a SAN that redirects logins connects it to another address).
    /// </summary>
    internal static string IscsiSession(string target, string portal) =>
        $"$p iscsiadm -m session -P 1 2>/dev/null | awk -v t='{target}' -v p='{portal},' '/^Target:/ {{ on = ($2 == t) }} on && /Persistent Portal:/ && index($3, p) == 1 {{ f = 1 }} END {{ exit !f }}' ";

    /// <summary>Stops (as "same") when a disk of the session is still held, mounted or open.</summary>
    private static string IscsiUnused(string target, string portal) =>
        $"for d in $($p iscsiadm -m session -P 3 2>/dev/null | awk -v t='{target}' -v p='{portal},' '/^Target:/ {{ on = ($2 == t) }} /Persistent Portal:/ {{ cur = (index($3, p) == 1) }} on && cur && /Attached scsi disk/ {{ print $4 }}'); do "
        + "[ -z \"$(ls /sys/block/$d/holders 2>/dev/null)\" ] && ! grep -q \"^/dev/$d\" /proc/mounts && ! { command -v fuser >/dev/null && $p fuser -s /dev/$d 2>/dev/null; } "
        + "|| { echo \"same: /dev/$d is still in use, so the old host stays logged in\"; exit 0; }; done; ";

    private static string Role(string resource, string role) =>
        $"r=$($p drbdadm role '{resource}' 2>/dev/null | head -n 1); case \"$r\" in {role}*) echo same;; *) $p drbdadm {role.ToLowerInvariant()} '{resource}' && echo changed;; esac";

    /// <summary>Mounts a file system, finding its device on this host by DRBD resource, LVM volume, multipath WWID or UUID.</summary>
    private static string MountScript(IReadOnlyDictionary<string, string> a)
    {
        var mountpoint = StorageTrace.Quoted(a["mountpoint"]);
        var fstype = Safe(a["fstype"]);
        var device = a.TryGetValue("drbd", out var drbd) ? $"d=$($p drbdadm sh-dev '{Safe(drbd)}' | head -n 1)"
            : a.TryGetValue("lv", out var lv) ? $"d='/dev/{Safe(lv)}'"
            : a.TryGetValue("wwid", out var wwid) ? $"d='/dev/disk/by-id/dm-uuid-mpath-{Safe(wwid)}'"
            : a.TryGetValue("uuid", out var uuid) ? $"d='UUID={Safe(uuid)}'"
            : $"d='{StorageTrace.Quoted(a["device"])}'";
        return $"mountpoint -q '{mountpoint}' && {{ echo same; exit 0; }}; {device}; $p mkdir -p '{mountpoint}' && $p mount -t '{fstype}' \"$d\" '{mountpoint}' || exit 1; "
            + $"mountpoint -q '{mountpoint}' && echo changed || {{ echo 'mount reported success, but {mountpoint} is not mounted' >&2; exit 1; }}";
    }

    private static string CheckPaths(string paths)
    {
        var script = new StringBuilder("m=0; ");
        foreach (var path in paths.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var quoted = StorageTrace.Quoted(path);
            script.Append(CultureInfo.InvariantCulture, $"[ -e '{quoted}' ] || {{ echo '{quoted} is missing on this host' >&2; m=1; }}; ");
        }

        return script.Append("[ $m = 0 ] && echo same; exit $m").ToString();
    }

    /// <summary>A name from the hosts' own tools (target, portal, resource, volume, WWID…), checked so it can go in single quotes.</summary>
    private static string Safe(string value) =>
        SafeName().IsMatch(value) ? value : throw new SessionException(SessionErrorCodes.BadRequest, $"NKeeper can't use “{value}” in a command.");

    [GeneratedRegex(@"^[A-Za-z0-9\[][A-Za-z0-9._:@+\[\]/=-]{0,254}$")]
    private static partial Regex SafeName();
}
