using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;

namespace NKeeper.Api.Ase;

public sealed record AseCopyCheckRequest
{
    [Required]
    [StringLength(64)]
    public string TargetSessionId { get; init; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string Database { get; init; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string TargetDatabase { get; init; } = string.Empty;
}

public sealed record AseCopyStepRequest
{
    [Required]
    [StringLength(64)]
    public string TargetSessionId { get; init; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string Database { get; init; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string TargetDatabase { get; init; } = string.Empty;

    /// <summary>"create", "dump", "load", "online" or "settrunc".</summary>
    [Required]
    [RegularExpression("^(create|dump|load|online|settrunc)$")]
    public string Step { get; init; } = string.Empty;

    /// <summary>Where the source's Backup Server writes the dump (on the remote Backup Server's machine when there is one).</summary>
    [StringLength(255)]
    public string? DumpPath { get; init; }

    /// <summary>Where the target's Backup Server reads it.</summary>
    [StringLength(255)]
    public string? LoadPath { get; init; }

    /// <summary>A Backup Server the source knows, by its network name (sysservers.srvnetname: dump … at looks it up in the interfaces file).</summary>
    [StringLength(255)]
    public string? RemoteBackupServer { get; init; }

    public bool Compress { get; init; }

    /// <summary>The create step's statement, as the user reviewed it: must be a CREATE DATABASE … FOR LOAD of the target.</summary>
    [StringLength(4000)]
    public string? CreateSql { get; init; }
}

public sealed record AseCopyServer(string Server, string Version, int PageSize, int? CharsetId, int? SortOrderId);

/// <summary>What copying needs and what stands in its way.</summary>
/// <param name="Blockers">Reasons it can't work as it is; empty when it can.</param>
/// <param name="CreateSql">When the target database doesn't exist: a CREATE DATABASE … FOR LOAD big enough, on the target's roomiest devices.</param>
/// <param name="BackupServers">Network names of servers the source knows (sysservers.srvnetname), to dump through the target's Backup Server.</param>
/// <param name="Replicated">The source holds a replication truncation point: the copy will too, unless it's cleared.</param>
public sealed record AseCopyPlan(
    AseCopyServer Source,
    AseCopyServer Target,
    double DataMb,
    double LogMb,
    bool TargetExists,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string? CreateSql,
    IReadOnlyList<string> BackupServers,
    bool Replicated);

/// <summary>
/// Copies a database to another server as ASE does it: <c>dump database</c> on the source (through a remote Backup
/// Server to write it on the target's machine, or to storage both can reach), <c>load database</c> on the target,
/// then <c>online database</c>. Each step runs when the user asks, for as long as it takes, and is audited.
/// </summary>
internal static partial class AseCopy
{
    /// <summary>The network names <c>dump … at</c> takes: it finds them in the interfaces file, not by sysservers' own names.</summary>
    public const string KnownBackupServersSql = "SELECT DISTINCT srvnetname FROM master..sysservers WHERE srvnetname IS NOT NULL";

    public static async Task<AseCopyPlan> CheckAsync(DbConnection source, DbConnection target, AseCopyCheckRequest request, bool sameServer, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        RequireName(request.Database);
        RequireName(request.TargetDatabase);

        var from = await ServerAsync(source, cancellationToken);
        var to = await ServerAsync(target, cancellationToken);
        if (from.PageSize != to.PageSize)
        {
            blockers.Add($"The servers' page sizes differ ({from.PageSize / 1024}K here, {to.PageSize / 1024}K there): a dump only loads on a server with the same page size.");
        }

        if (VersionKey(to.Version).CompareTo(VersionKey(from.Version)) < 0)
        {
            blockers.Add($"The target runs an older ASE ({to.Version}) than the source ({from.Version}): a dump doesn't load on an older version.");
        }

        if (from.CharsetId != to.CharsetId || from.SortOrderId != to.SortOrderId)
        {
            warnings.Add("The servers' default character set or sort order differ: the load may refuse it, or text may need converting.");
        }

        if (AseDba.SystemDatabases.Contains(request.TargetDatabase))
        {
            blockers.Add($"{request.TargetDatabase} is a system database: choose another target.");
        }

        if (sameServer && string.Equals(request.Database, request.TargetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("The target is the source itself: choose another database name, or another server.");
        }

        var sourceSpace = (await AseDba.DatabaseSpaceAsync(source, cancellationToken)).FirstOrDefault(d => string.Equals(d.Name, request.Database, StringComparison.OrdinalIgnoreCase));
        if (sourceSpace is null)
        {
            throw new SqlExplorerException($"{request.Database} isn't an online database on {from.Server}.");
        }

        var targetSpace = (await AseDba.DatabaseSpaceAsync(target, cancellationToken)).FirstOrDefault(d => string.Equals(d.Name, request.TargetDatabase, StringComparison.OrdinalIgnoreCase));
        var targetExists = targetSpace is not null || await ExistsAsync(target, request.TargetDatabase, cancellationToken);
        string? createSql = null;
        if (targetSpace is not null)
        {
            var dataShort = sourceSpace.DataMb - targetSpace.DataMb;
            var logShort = sourceSpace.LogMb - targetSpace.LogMb;
            if (dataShort > 0.5 || logShort > 0.5)
            {
                blockers.Add(
                    $"{request.TargetDatabase} on {to.Server} is smaller than {request.Database} (data {AseDba.Mb(targetSpace.DataMb)} vs {AseDba.Mb(sourceSpace.DataMb)} MB, log {AseDba.Mb(targetSpace.LogMb)} vs {AseDba.Mb(sourceSpace.LogMb)} MB): grow it with alter database first.");
            }

            warnings.Add($"{request.TargetDatabase} already exists on {to.Server}: loading replaces everything in it.");
            var users = await DbQuery.ListAsync(
                target,
                "SELECT spid, suser_name(suid), hostname, program_name FROM master..sysprocesses WHERE dbid = db_id(@db) AND spid <> @@spid",
                [("@db", request.TargetDatabase)],
                r => $"spid {DbQuery.Int(r, 0)} ({DbQuery.Text(r, 1)} from {DbQuery.Text(r, 2)}, {DbQuery.Text(r, 3)})",
                cancellationToken);
            if (users.Count > 0)
            {
                blockers.Add($"{request.TargetDatabase} is in use on {to.Server}: {string.Join("; ", users.Take(10))}. A load needs it to itself.");
            }
        }
        else if (targetExists)
        {
            warnings.Add($"{request.TargetDatabase} exists on {to.Server} but is offline or waiting for a load: its size couldn't be checked.");
        }
        else
        {
            var devices = await AseDba.DeviceSpaceAsync(target, cancellationToken);
            createSql = CreateFor(request.TargetDatabase, sourceSpace, devices, blockers, to.Server);
        }

        var backupServers = await DbQuery.ListAsync(
            source, KnownBackupServersSql + " AND srvname <> @@servername ORDER BY 1", [], r => DbQuery.Text(r, 0)!, cancellationToken);

        var replicated = (await DbQuery.ListAsync(
            source,
            "SELECT count(*) FROM master..syslogshold WHERE dbid = db_id(@db) AND name = '$replication_truncation_point'",
            [("@db", request.Database)],
            r => DbQuery.Int(r, 0),
            cancellationToken))[0] > 0;
        if (replicated)
        {
            warnings.Add($"{request.Database} is replicated: the copy keeps its replication truncation point, so its log won't truncate until that's cleared (the last step does it).");
        }

        warnings.Add("Users in the copy map to logins by suid: on another server, check them afterwards (sp_helpuser, or the logins' suids).");
        return new AseCopyPlan(from, to, sourceSpace.DataMb, sourceSpace.LogMb, targetExists, blockers, warnings, createSql, backupServers, replicated);
    }

    public static async Task<ToolSection> RunAsync(DbConnection source, DbConnection target, AseCopyStepRequest request, IReadOnlyList<string> knownServers, CancellationToken cancellationToken)
    {
        var (onSource, title, statements) = Plan(request, knownServers);
        var connection = onSource ? source : target;

        var messages = new List<SqlOutput>();
        var ase = (AseConnection)connection;

        void OnInfo(object sender, AseInfoMessageEventArgs e)
        {
            lock (messages)
            {
                foreach (AseError error in e.Errors)
                {
                    if (!string.IsNullOrWhiteSpace(error.Message))
                    {
                        messages.Add(new SqlOutput("message", error.Message.TrimEnd()));
                    }
                }
            }
        }

        ase.InfoMessage += OnInfo;
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var statement in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                // A dump or load takes as long as the database is big.
                command.CommandTimeout = 0;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            lock (messages)
            {
                messages.Add(new SqlOutput("message", $"Finished in {watch.Elapsed:hh\\:mm\\:ss}."));
            }

            return new ToolSection(title, string.Join("\n", statements), [.. messages], null);
        }
        catch (AseException ex)
        {
            var error = string.Join(" ", ex.Errors.Cast<AseError>().Where(x => x.Severity > 10).Select(x => x.Message.Trim()).DefaultIfEmpty(ex.Message));
            return new ToolSection(title, string.Join("\n", statements), [.. messages], error);
        }
        finally
        {
            ase.InfoMessage -= OnInfo;
        }
    }

    /// <summary>
    /// What a step runs, and where: every name and path checked first (names as ASE accepts them, paths without
    /// quotes, a remote Backup Server the source knows), so nothing the user typed reaches SQL unchecked.
    /// </summary>
    internal static (bool OnSource, string Title, string[] Statements) Plan(AseCopyStepRequest request, IReadOnlyList<string> knownServers)
    {
        RequireName(request.Database);
        RequireName(request.TargetDatabase);
        var target = AseDba.Quote(request.TargetDatabase);
        return request.Step switch
        {
            "create" => (false, "Create the target database", [CreateStatement(request)]),
            "dump" => (true, $"Dump {request.Database}", [DumpStatement(request, knownServers)]),
            "load" => (false, $"Load {request.TargetDatabase}", [$"load database {target} from {Literal(RequirePath(request.LoadPath ?? request.DumpPath))}"]),
            "online" => (false, $"Bring {request.TargetDatabase} online", [$"online database {target}"]),
            "settrunc" => (false, "Clear the replication truncation point", [$"use {target}", "dbcc settrunc('ltm', 'ignore')", "use master"]),
            _ => throw new SqlExplorerException("Unknown step."),
        };
    }

    private static string DumpStatement(AseCopyStepRequest request, IReadOnlyList<string> knownServers)
    {
        var at = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.RemoteBackupServer))
        {
            var server = request.RemoteBackupServer.Trim();
            if (!Name().IsMatch(server) || !knownServers.Contains(server, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlExplorerException($"{server} isn't a server the source knows (sysservers.srvnetname).");
            }

            at = $" at {server}";
        }

        return $"dump database {AseDba.Quote(request.Database)} to {Literal(RequirePath(request.DumpPath))}{at}{(request.Compress ? " with compression = 101" : string.Empty)}";
    }

    private static string CreateStatement(AseCopyStepRequest request)
    {
        var sql = request.CreateSql?.Trim() ?? string.Empty;
        if (!CreateForLoad().IsMatch(sql) || sql.Contains(';', StringComparison.Ordinal) || sql.Contains("\ngo", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlExplorerException("The create step runs one CREATE DATABASE … FOR LOAD statement.");
        }

        var named = CreatedName().Match(sql);
        if (!named.Success || !string.Equals(named.Groups[1].Value.Trim('[', ']', '"'), request.TargetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlExplorerException($"The statement must create {request.TargetDatabase}.");
        }

        return sql;
    }

    private static string? CreateFor(string name, AseDba.DatabaseSpace source, IReadOnlyList<AseDba.DeviceSpace> devices, List<string> blockers, string server)
    {
        var usable = devices.Where(d => !d.Name.Equals("master", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.FreeMb).ToList();
        var dataMb = Math.Ceiling(source.DataMb);
        var logMb = Math.Ceiling(source.LogMb);
        var data = usable.FirstOrDefault(d => d.FreeMb >= dataMb);
        if (data is null)
        {
            blockers.Add($"No device on {server} has {dataMb:0} MB free for the data: add one (disk init) or create {name} yourself.");
            return null;
        }

        if (logMb < 1)
        {
            return $"create database {AseDba.Quote(name)} on {data.Name} = '{dataMb:0}M' for load";
        }

        // The log on a device of its own when there's one with room, as the source has it.
        var log = usable.FirstOrDefault(d => d != data && d.FreeMb >= logMb)
            ?? (data.FreeMb >= dataMb + logMb ? data : null);
        if (log is null)
        {
            blockers.Add($"No device on {server} has {logMb:0} MB free for the log: add one (disk init) or create {name} yourself.");
            return null;
        }

        return $"create database {AseDba.Quote(name)} on {data.Name} = '{dataMb:0}M' log on {log.Name} = '{logMb:0}M' for load";
    }

    private static async Task<AseCopyServer> ServerAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var row = (await DbQuery.ListAsync(
            connection,
            """
            SELECT @@servername, @@version, @@maxpagesize,
                   (SELECT value FROM master..sysconfigures WHERE config = 131),
                   (SELECT value FROM master..sysconfigures WHERE config = 123)
            """,
            [],
            r => (Name: DbQuery.Text(r, 0), Version: DbQuery.Text(r, 1), Page: DbQuery.Int(r, 2), Charset: r.IsDBNull(3) ? (int?)null : DbQuery.Int(r, 3), Sort: r.IsDBNull(4) ? (int?)null : DbQuery.Int(r, 4)),
            cancellationToken))[0];
        var parts = row.Version?.Split('/') ?? [];
        return new AseCopyServer(row.Name ?? "?", parts.Length > 1 ? parts[1] : row.Version ?? string.Empty, row.Page, row.Charset, row.Sort);
    }

    private static async Task<bool> ExistsAsync(DbConnection connection, string name, CancellationToken cancellationToken) =>
        (await DbQuery.ListAsync(connection, "SELECT count(*) FROM master..sysdatabases WHERE name = @db", [("@db", name)], r => DbQuery.Int(r, 0), cancellationToken))[0] > 0;

    /// <summary>"16.0 SP03 PL08" → 16, 0, 3, 8: compared in order.</summary>
    internal static VersionKeyValue VersionKey(string version) => new([.. Numbers().Matches(version).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).Take(4)]);

    internal sealed record VersionKeyValue(int[] Parts) : IComparable<VersionKeyValue>
    {
        public int CompareTo(VersionKeyValue? other)
        {
            for (var i = 0; i < Math.Max(Parts.Length, other?.Parts.Length ?? 0); i++)
            {
                var a = i < Parts.Length ? Parts[i] : 0;
                var b = other is not null && i < other.Parts.Length ? other.Parts[i] : 0;
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            return 0;
        }
    }

    private static void RequireName(string name)
    {
        if (!Name().IsMatch(name))
        {
            throw new SqlExplorerException($"“{name}” isn't a database name ASE accepts.");
        }
    }

    private static string RequirePath(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || !SafePath().IsMatch(trimmed))
        {
            throw new SqlExplorerException("Give the dump file's path (no quotes).");
        }

        return trimmed;
    }

    private static string Literal(string text) => $"'{text}'";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#@]{0,254}$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[^'""\r\n;]{1,255}$")]
    private static partial Regex SafePath();

    [GeneratedRegex(@"^create\s+database\s+.+\s+for\s+load$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateForLoad();

    [GeneratedRegex(@"^create\s+database\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedName();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
