using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Ssh;
using Renci.SshNet;

namespace NKeeper.Api.Selinux;

/// <summary>An installed policy module. The same name can be installed at several priorities; the highest is in force.</summary>
/// <param name="Priority">100 is the distribution's; <c>semodule -i</c> installs local ones at 400.</param>
public sealed record SelinuxModule(int Priority, string Name, string Format);

/// <summary>Something SELinux denied, or an error it logged.</summary>
/// <param name="EventId">The audit event's serial number, to find it again with <c>ausearch -a</c>.</param>
/// <param name="Blocked">False when SELinux only logged it, in permissive mode or for a permissive domain.</param>
/// <param name="Permissions">What was denied, e.g. <c>read</c>; empty for an error.</param>
/// <param name="Process">The command, or the path of its program.</param>
/// <param name="Target">The file or other thing it wanted.</param>
/// <param name="TargetClass">What kind of thing that is, e.g. <c>file</c>.</param>
/// <param name="Raw">The record as the audit log has it.</param>
public sealed record SelinuxDenial(
    long EventId, DateTimeOffset Time, string Type, bool Blocked, IReadOnlyList<string> Permissions, string? Process, string? Pid, string? Target, string? TargetClass, string? SourceContext, string? TargetContext, string Raw);

/// <summary>A host's SELinux: its mode, policy modules and what it has denied.</summary>
/// <param name="Mode"><c>Enforcing</c>, <c>Permissive</c> or <c>Disabled</c>; null when the host has no SELinux tools.</param>
/// <param name="AuditProblem">Why the denials couldn't be read, e.g. auditd isn't running.</param>
public sealed record SelinuxStatus(string? Mode, IReadOnlyList<SelinuxModule> Modules, IReadOnlyList<SelinuxDenial> Denials, bool DenialsTruncated, string? AuditProblem);

/// <summary>
/// A host's SELinux, over SSH, with its own tools: <c>semodule</c> for modules, <c>ausearch</c> for the
/// audit log, and <c>audit2allow</c>, <c>checkmodule</c> and <c>semodule_package</c> for new policies.
/// Reading needs root, and so does changing: root, or sudo without a password.
/// </summary>
internal static partial class SelinuxHost
{
    public const int DistributionPriority = 100;
    public const int MaxEvents = 100;

    private const int MaxDenialLines = 1000;
    private const string AuditTypes = "AVC,USER_AVC,SELINUX_ERR";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromSeconds(280);

    /// <summary>
    /// ausearch only takes times in the locale's format (<c>%x</c>, with a 2-digit year in the C locale)
    /// and the host's zone, so the host's own date turns each one from an instant, in the C locale that
    /// ausearch is then run in. Its output has the events' other records (SYSCALL, PROCTITLE…), which
    /// are dropped there so that they don't count towards the limit; what it says isn't a record.
    /// </summary>
    internal static string StatusScript(DateTimeOffset? since, DateTimeOffset? until)
    {
        var times = new StringBuilder();
        void Time(string option, DateTimeOffset? time)
        {
            if (time is { } value)
            {
                times.Append(CultureInfo.InvariantCulture, $" {option} $(LC_ALL=C date -d @{value.ToUnixTimeSeconds()} '+%x %H:%M:%S')");
            }
        }

        Time("-ts", since);
        Time("-te", until);
        return SshScript.RootIfAllowed
            + "command -v getenforce >/dev/null 2>&1 || { echo '@@none'; exit 0; }; "
            + "echo '@@mode'; getenforce 2>/dev/null; "
            + "echo '@@modules'; $p semodule -lfull 2>&1; "
            + $"echo '@@audit'; $p env LC_ALL=C ausearch -m {AuditTypes} --raw{times} 2>&1 | awk '!/^type=/ || /^type=(AVC|USER_AVC|SELINUX_ERR) /' | tail -n {MaxDenialLines + 1}; exit 0";
    }

    public static async Task<SelinuxStatus> GetStatusAsync(SshClient client, DateTimeOffset? since, DateTimeOffset? until, CancellationToken cancellationToken)
    {
        var (_, output, errors) = await SshScript.RunAsync(client, StatusScript(since, until), ReadTimeout, "Reading SELinux", cancellationToken);
        return ParseStatus(output.Length > 0 ? output : errors);
    }

    internal static SelinuxStatus ParseStatus(string output)
    {
        var (text, limited) = SshScript.WithoutLimitedMarker(output);
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..].Trim()] = [];
            }
            else if (line.Length > 0)
            {
                current?.Add(line);
            }
        }

        if (sections.ContainsKey("none"))
        {
            return new SelinuxStatus(null, [], [], false, null);
        }

        var modules = sections.GetValueOrDefault("modules") ?? [];
        var problem = modules.FirstOrDefault(l => !ModuleLine().IsMatch(l));
        if (limited && modules.Count > 0 && problem is not null)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "Managing SELinux needs root. Sign in as root, or as a user who may use sudo without a password.");
        }

        var audit = sections.GetValueOrDefault("audit") ?? [];
        var records = audit.Where(l => l.StartsWith("type=", StringComparison.Ordinal)).ToList();
        return new SelinuxStatus(
            sections.GetValueOrDefault("mode")?.FirstOrDefault()?.Trim(),
            [.. modules.Select(ParseModule).OfType<SelinuxModule>().OrderBy(m => m.Name, StringComparer.Ordinal).ThenByDescending(m => m.Priority)],
            [.. records.TakeLast(MaxDenialLines).Select(ParseDenial).OfType<SelinuxDenial>().OrderByDescending(d => d.Time).ThenByDescending(d => d.EventId)],
            records.Count > MaxDenialLines,
            records.Count > 0 ? null : AuditProblem(audit));
    }

    /// <summary>What ausearch said when it found no records, unless that's just that there are none.</summary>
    private static string? AuditProblem(List<string> lines)
    {
        var said = lines.Where(l => !l.StartsWith("NOTE", StringComparison.Ordinal) && !l.Contains("no matches", StringComparison.OrdinalIgnoreCase)).ToList();
        if (said.Count == 0)
        {
            return null;
        }

        var text = string.Join(' ', said);
        return text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? "This host has no ausearch. Install its audit package (audit on Red Hat, auditd on Debian) to see denials."
            : text.Contains("No such file", StringComparison.OrdinalIgnoreCase)
                ? "This host has no audit log, so denials can't be read. Check that auditd is running."
                : text;
    }

    private static SelinuxModule? ParseModule(string line)
    {
        var match = ModuleLine().Match(line);
        return match.Success ? new SelinuxModule(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, match.Groups[3].Value) : null;
    }

    /// <summary>An AVC, USER_AVC or SELINUX_ERR record; null for what isn't a denial, such as an auditallow's "granted".</summary>
    internal static SelinuxDenial? ParseDenial(string line)
    {
        var header = AuditHeader().Match(line);
        if (!header.Success
            || !decimal.TryParse(header.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds > 253402300799m
            || !long.TryParse(header.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var time = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        var type = header.Groups[1].Value;
        var permissions = Permissions().Match(line);
        if (!permissions.Success)
        {
            return type == "SELINUX_ERR" ? new SelinuxDenial(id, time, type, true, [], null, null, null, null, null, null, line) : null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match field in Field().Matches(line[(permissions.Index + permissions.Length)..]))
        {
            fields.TryAdd(field.Groups[1].Value, field.Groups[2].Value.Trim('"', '\''));
        }

        return new SelinuxDenial(
            id,
            time,
            type,
            fields.GetValueOrDefault("permissive") != "1",
            permissions.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            fields.GetValueOrDefault("comm") ?? fields.GetValueOrDefault("exe"),
            fields.GetValueOrDefault("pid"),
            fields.GetValueOrDefault("path") ?? fields.GetValueOrDefault("name") ?? fields.GetValueOrDefault("dest") ?? fields.GetValueOrDefault("capability"),
            fields.GetValueOrDefault("tclass"),
            fields.GetValueOrDefault("scontext"),
            fields.GetValueOrDefault("tcontext"),
            line);
    }

    /// <summary>A policy's source for these audit events, from <c>audit2allow</c>, for the user to read before installing.</summary>
    public static async Task<string> GenerateAsync(SshClient client, string name, IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        var (exit, output, errors) = await SshScript.RunAsync(client, GenerateScript(name, eventIds), ReadTimeout, "Generating the policy", cancellationToken);
        var (source, _) = SshScript.WithoutLimitedMarker(output);
        if (exit != 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"Couldn't generate a policy: {errors.Trim()}");
        }

        return source.Contains("allow ", StringComparison.Ordinal) || source.Contains("module ", StringComparison.Ordinal)
            ? source.Trim() + "\n"
            : throw new SessionException(SessionErrorCodes.ConnectionFailed, "audit2allow found nothing to allow in those events. Its log entries may have rotated away.");
    }

    internal static string GenerateScript(string name, IReadOnlyList<long> eventIds)
    {
        var ids = string.Join(' ', eventIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return SshScript.RootIfAllowed
            + Needs("audit2allow", "policycoreutils-python-utils on Red Hat, policycoreutils-python on older ones, selinux-policy-dev on Debian")
            + $"for id in {ids}; do $p ausearch -a \"$id\" --raw 2>/dev/null; done | $p audit2allow -m {SshScript.Quote(name)}";
    }

    /// <summary>Compiles a policy's source and installs it.</summary>
    public static async Task InstallAsync(SshClient client, string name, string source, CancellationToken cancellationToken)
    {
        var (exit, _, errors) = await SshScript.RunAsync(client, InstallScript(name, source), ChangeTimeout, "Installing the policy", cancellationToken);
        if (exit != 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The policy wasn't installed: {Trimmed(errors)}");
        }
    }

    internal static string InstallScript(string name, string source)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        var file = SshScript.Quote(name);
        return SshScript.RootIfAllowed
            + Needs("checkmodule", "checkpolicy")
            + Needs("semodule_package", "policycoreutils or checkpolicy")
            + "d=$(mktemp -d) || exit 1; trap 'rm -rf \"$d\"' EXIT; cd \"$d\" || exit 1; "
            + $"f={file}; printf '%s' '{encoded}' | base64 -d > \"$f.te\" && "
            + "checkmodule -M -m -o \"$f.mod\" \"$f.te\" && semodule_package -o \"$f.pp\" -m \"$f.mod\" && $p semodule -i \"$f.pp\"";
    }

    /// <summary>Removes one installed copy of a module.</summary>
    public static async Task RemoveAsync(SshClient client, SelinuxModule module, CancellationToken cancellationToken)
    {
        var (exit, _, errors) = await SshScript.RunAsync(client, RemoveScript(module), ChangeTimeout, "Removing the policy", cancellationToken);
        if (exit != 0)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"{module.Name} wasn't removed: {Trimmed(errors)}");
        }
    }

    internal static string RemoveScript(SelinuxModule module) =>
        SshScript.RootIfAllowed + string.Create(CultureInfo.InvariantCulture, $"$p semodule -X {module.Priority} -r {SshScript.Quote(module.Name)}");

    /// <summary>
    /// The module a policy's source declares, from its <c>module name 1.0;</c> line; null when it has
    /// none. Comments before it are allowed, as audit2allow writes none but people do.
    /// </summary>
    public static string? DeclaredModule(string source)
    {
        foreach (var line in source.Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = ModuleDeclaration().Match(line);
            return match.Success ? match.Groups[1].Value : null;
        }

        return null;
    }

    public static bool IsModuleName(string name) => ModuleName().IsMatch(name);

    private static string Needs(string tool, string package) =>
        $"command -v {tool} >/dev/null 2>&1 || {{ echo 'This host needs {tool} (package {package}).' >&2; exit 127; }}; ";

    private static string Trimmed(string errors) =>
        errors.Trim() is { Length: > 0 } text ? text : "the tools gave no reason.";

    [GeneratedRegex(@"^(\d+)\s+(\S+)\s+(\S+)\s*(?:disabled\s*)?$")]
    private static partial Regex ModuleLine();

    [GeneratedRegex(@"^type=(AVC|USER_AVC|SELINUX_ERR) msg=audit\((\d+(?:\.\d+)?):(\d+)\)")]
    private static partial Regex AuditHeader();

    [GeneratedRegex(@"avc:\s+denied\s+\{([^}]*)\}\s+for")]
    private static partial Regex Permissions();

    [GeneratedRegex("""\b([a-z_]+)=("[^"]*"|'[^']*'|[^\s']+)""")]
    private static partial Regex Field();

    [GeneratedRegex(@"^module\s+([A-Za-z][A-Za-z0-9_.-]*)\s+[0-9.]+\s*;")]
    private static partial Regex ModuleDeclaration();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ModuleName();
}
