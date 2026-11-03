using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AdoNetCore.AseClient;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>
/// SAP ASE's query runner (batches split on GO, PRINT and server messages under Messages), object
/// explorer (each database's system tables), scripts (procedures, views and triggers from syscomments;
/// tables from syscolumns) and blocking manager (master..sysprocesses).
/// </summary>
internal sealed class AseDialect : IDatabaseDialect
{
    public static readonly AseDialect Instance = new();

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "tempdb", "sybsystemprocs", "sybsystemdb", "sybsecurity", "sybmgmtdb", "dbccdb", "dbccalt", "sybpcidb", "sybdiag", "pubs2", "pubs3",
    };

    private static string Quote(string name) => SqlObjectExplorer.Quote(name);

    public async Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await DbQuery.ListAsync(connection, "SELECT db_name()", [], r => DbQuery.Text(r, 0) ?? string.Empty, cancellationToken))[0];

    public async Task<IReadOnlyList<string>> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(connection, "SELECT name FROM master..sysdatabases ORDER BY name", [], r => DbQuery.Text(r, 0)!, cancellationToken);

    public async Task<string> ChangeDatabaseAsync(DbConnection connection, string name, CancellationToken cancellationToken)
    {
        await DbQuery.ExecuteAsync(connection, $"USE {Quote(name)}", cancellationToken);
        return await CurrentDatabaseAsync(connection, cancellationToken);
    }

    // ---- Running scripts ----

    public async Task<SqlQueryResult> RunAsync(DbConnection connection, string script, int lineOffset, CancellationToken cancellationToken)
    {
        var ase = (AseConnection)connection;
        var output = new List<SqlOutput>();
        var sent = 0;
        var cancelled = false;
        var hasErrors = false;
        var watch = Stopwatch.StartNew();
        var batchStart = 1;

        void OnInfo(object sender, AseInfoMessageEventArgs e)
        {
            lock (output)
            {
                foreach (AseError error in e.Errors)
                {
                    if (error.Severity <= 10)
                    {
                        output.Add(new SqlOutput("message", error.Message.TrimEnd()));
                    }
                }
            }
        }

        ase.InfoMessage += OnInfo;
        try
        {
            foreach (var batch in SqlBatches.Split(script))
            {
                batchStart = batch.StartLine;
                for (var run = 0; run < batch.Count; run++)
                {
                    await using var command = ase.CreateCommand();
                    command.CommandText = batch.Text;
                    command.CommandTimeout = 0;
                    await using var registration = cancellationToken.Register(() =>
                    {
                        try
                        {
                            command.Cancel();
                        }
                        catch (Exception ex) when (ex is AseException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
                        {
                        }
                    });

                    try
                    {
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        do
                        {
                            if (reader.FieldCount > 0)
                            {
                                int slot;
                                lock (output)
                                {
                                    slot = output.Count;
                                    output.Add(new SqlOutput("results"));
                                }

                                var result = await ReadResultAsync(reader, SqlQueryRunner.MaxRowsTotal - sent, cancellationToken);
                                sent += result.Rows!.Count;
                                lock (output)
                                {
                                    output[slot] = result;
                                    output.Add(new SqlOutput("rows", $"({result.RowCount} {(result.RowCount == 1 ? "row" : "rows")} affected)", RowCount: result.RowCount));
                                }
                            }
                        }
                        while (await reader.NextResultAsync(cancellationToken));

                        if (reader.RecordsAffected >= 0)
                        {
                            lock (output)
                            {
                                output.Add(new SqlOutput("rows", $"({reader.RecordsAffected} {(reader.RecordsAffected == 1 ? "row" : "rows")} affected)", RowCount: reader.RecordsAffected));
                            }
                        }
                    }
                    catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or AseException)
                    {
                        cancelled = true;
                        output.Add(new SqlOutput("error", "Query was cancelled by user."));
                        break;
                    }
                    catch (AseException ex)
                    {
                        hasErrors = true;
                        lock (output)
                        {
                            foreach (AseError error in ex.Errors)
                            {
                                output.Add(error.Severity > 10
                                    ? new SqlOutput(
                                        "error",
                                        error.Message.TrimEnd(),
                                        Number: error.MessageNumber,
                                        Severity: error.Severity,
                                        Procedure: string.IsNullOrEmpty(error.ProcName) ? null : error.ProcName,
                                        Line: error.LineNum <= 0 ? null : string.IsNullOrEmpty(error.ProcName) ? batchStart + lineOffset + error.LineNum - 1 : error.LineNum)
                                    : new SqlOutput("message", error.Message.TrimEnd()));
                            }
                        }

                        if (connection.State != ConnectionState.Open)
                        {
                            return new SqlQueryResult(output, string.Empty, watch.ElapsedMilliseconds, false, true, true);
                        }
                    }
                }

                if (cancelled)
                {
                    break;
                }
            }
        }
        finally
        {
            ase.InfoMessage -= OnInfo;
        }

        string database;
        try
        {
            database = connection.State == ConnectionState.Open ? await CurrentDatabaseAsync(connection, CancellationToken.None) : string.Empty;
        }
        catch (AseException)
        {
            database = string.Empty;
        }

        return new SqlQueryResult(output, database, watch.ElapsedMilliseconds, cancelled, connection.State != ConnectionState.Open, hasErrors);
    }

    private static async Task<SqlOutput> ReadResultAsync(DbDataReader reader, int budget, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i =>
            {
                var name = reader.GetName(i);
                return new SqlColumn(string.IsNullOrEmpty(name) ? "(No column name)" : name, reader.GetDataTypeName(i), DbValueText.IsNumeric(reader.GetFieldType(i)));
            })
            .ToList();
        var limit = Math.Max(0, Math.Min(SqlQueryRunner.MaxRowsPerResult, budget));
        var rows = new List<string?[]>();
        long count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (count++ < limit)
            {
                var row = new string?[columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : DbValueText.Format(reader.GetValue(i), SqlQueryRunner.MaxCharsPerValue);
                }

                rows.Add(row);
            }
        }

        return new SqlOutput("results", Columns: columns, Rows: rows, RowCount: count, Truncated: count > rows.Count);
    }

    // ---- Object explorer ----

    public async Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken)
    {
        var db = node.Database;
        return node.Kind switch
        {
            Kinds.Server =>
            [
                Folder(Kinds.Databases, "Databases"),
                Folder(Kinds.ServerSecurity, "Security"),
                Folder(Kinds.Management, "Management"),
            ],
            Kinds.Databases => [Folder(Kinds.SystemDatabases, "System Databases"), .. await DatabaseNodesAsync(connection, system: false, cancellationToken)],
            Kinds.SystemDatabases => await DatabaseNodesAsync(connection, system: true, cancellationToken),
            Kinds.Database =>
            [
                Folder(Kinds.Tables, "Tables", db),
                Folder(Kinds.Views, "Views", db),
                Folder(Kinds.Programmability, "Programmability", db),
                Folder(Kinds.DatabaseSecurity, "Security", db),
            ],
            Kinds.Tables => [Folder(Kinds.SystemTables, "System Tables", db), .. await ObjectsAsync(connection, Required(db), "U", Kinds.Table, cancellationToken)],
            Kinds.SystemTables => await ObjectsAsync(connection, Required(db), "S", Kinds.Table, cancellationToken),
            Kinds.Views => await ObjectsAsync(connection, Required(db), "V", Kinds.View, cancellationToken),
            Kinds.Table => ObjectFolders(node, Kinds.Columns, Kinds.Keys, Kinds.Indexes, Kinds.Triggers),
            Kinds.View => ObjectFolders(node, Kinds.Columns),
            Kinds.Programmability => [Folder(Kinds.Procedures, "Stored Procedures", db), Folder(Kinds.Functions, "Functions", db)],
            Kinds.Procedures => await ObjectsAsync(connection, Required(db), "P", Kinds.Procedure, cancellationToken),
            Kinds.Functions => await ObjectsAsync(connection, Required(db), "SF", Kinds.Function, cancellationToken),
            Kinds.Procedure or Kinds.Function => await ColumnsAsync(connection, Required(db), RequiredId(node), parameters: true, cancellationToken),
            Kinds.Columns => await ColumnsAsync(connection, Required(db), RequiredId(node), parameters: false, cancellationToken),
            Kinds.Keys => await KeysAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.Indexes => await DbQuery.ListAsync(
                connection,
                $"SELECT name, indid, status, status2 FROM {Quote(Required(db))}..sysindexes WHERE id = @id AND indid > 0 AND indid < 255 ORDER BY name",
                [("@id", RequiredId(node))],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Index,
                    Label = DbQuery.Text(r, 0)!,
                    Detail = $"{((DbQuery.Int(r, 2) & 2) != 0 ? "Unique, " : "")}{(DbQuery.Int(r, 1) == 1 || (DbQuery.Int(r, 3) & 512) != 0 ? "clustered" : "nonclustered")}",
                },
                cancellationToken),
            Kinds.Triggers => await DbQuery.ListAsync(
                connection,
                $"""
                SELECT u.name, o.name, o.id FROM {Quote(Required(db))}..sysobjects o JOIN {Quote(Required(db))}..sysusers u ON u.uid = o.uid
                WHERE o.type = 'TR' AND o.deltrig = @id ORDER BY o.name
                """,
                [("@id", RequiredId(node))],
                r => new SqlObjectNode { Kind = Kinds.Trigger, Label = DbQuery.Text(r, 1)!, Database = db, Schema = DbQuery.Text(r, 0), Name = DbQuery.Text(r, 1), ObjectId = DbQuery.Int(r, 2) },
                cancellationToken),
            Kinds.DatabaseSecurity => [Folder(Kinds.Users, "Users", db), Folder(Kinds.Roles, "Groups", db)],
            Kinds.Users => await DbQuery.ListAsync(
                connection,
                $"SELECT u.name, l.name FROM {Quote(Required(db))}..sysusers u LEFT JOIN master..syslogins l ON l.suid = u.suid WHERE u.uid <> u.gid OR u.uid = 1 ORDER BY u.name",
                [],
                r => new SqlObjectNode { Kind = Kinds.User, Label = DbQuery.Text(r, 0)!, Detail = DbQuery.Text(r, 1) is { } login && login != DbQuery.Text(r, 0) ? $"login {login}" : null },
                cancellationToken),
            Kinds.Roles => await DbQuery.ListAsync(
                connection,
                $"SELECT name FROM {Quote(Required(db))}..sysusers WHERE uid = gid ORDER BY name",
                [],
                r => new SqlObjectNode { Kind = Kinds.Role, Label = DbQuery.Text(r, 0)! },
                cancellationToken),
            Kinds.ServerSecurity => [Folder(Kinds.Logins, "Logins"), Folder(Kinds.ServerRoles, "Roles")],
            Kinds.Logins => await DbQuery.ListAsync(
                connection,
                "SELECT name, dbname, status FROM master..syslogins ORDER BY name",
                [],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Login,
                    Label = DbQuery.Text(r, 0)!,
                    Detail = string.Join(", ", new[] { (DbQuery.Int(r, 2) & 2) != 0 ? "locked" : null, DbQuery.Text(r, 1) is { } d ? $"default {d}" : null }.Where(x => x is not null)),
                },
                cancellationToken),
            Kinds.ServerRoles => await DbQuery.ListAsync(
                connection, "SELECT name FROM master..syssrvroles ORDER BY name", [], r => new SqlObjectNode { Kind = Kinds.ServerRole, Label = DbQuery.Text(r, 0)! }, cancellationToken),
            Kinds.Management => [new SqlObjectNode { Kind = Kinds.Tool, Label = "Blocking Manager", Detail = "Blocking chains", Name = SqlObjectExplorer.BlockingManagerTool }],
            _ => throw new SqlExplorerException($"“{node.Kind}” has no children."),
        };
    }

    private static string Required(string? database) => string.IsNullOrEmpty(database) ? throw new SqlExplorerException("Choose a database.") : database;

    private static int RequiredId(SqlObjectNode node) => node.ObjectId ?? throw new SqlExplorerException("Choose an object.");

    private static SqlObjectNode Folder(string kind, string label, string? database = null) => new() { Kind = kind, Label = label, Expandable = true, Database = database };

    private static IReadOnlyList<SqlObjectNode> ObjectFolders(SqlObjectNode parent, params string[] kinds) =>
        [.. kinds.Select(kind => parent with
        {
            Kind = kind,
            Label = kind switch { Kinds.Columns => "Columns", Kinds.Keys => "Keys", Kinds.Indexes => "Indexes", _ => "Triggers" },
            Detail = null,
            Expandable = true,
        })];

    private static async Task<IReadOnlyList<SqlObjectNode>> DatabaseNodesAsync(DbConnection connection, bool system, CancellationToken cancellationToken)
    {
        var all = await DbQuery.ListAsync(
            connection,
            "SELECT name, status, status2 FROM master..sysdatabases ORDER BY name",
            [],
            r => (Name: DbQuery.Text(r, 0)!, Status: DbQuery.Int(r, 1), Status2: DbQuery.Int(r, 2)),
            cancellationToken);
        return [.. all
            .Where(d => (SystemDatabases.Contains(d.Name) || d.Name.StartsWith("tempdb", StringComparison.OrdinalIgnoreCase)) == system)
            .Select(d =>
            {
                // status2 16: offline; status 32/256: loading or suspect.
                var offline = (d.Status2 & 16) != 0 || (d.Status & 256) != 0;
                return new SqlObjectNode
                {
                    Kind = Kinds.Database,
                    Label = d.Name,
                    Database = d.Name,
                    Name = d.Name,
                    Expandable = !offline,
                    Detail = offline ? "offline" : (d.Status & 1024) != 0 ? "read-only" : null,
                };
            })];
    }

    /// <param name="type">sysobjects type: U (tables), S (system tables), V (views), P (procedures), SF (functions).</param>
    private static async Task<IReadOnlyList<SqlObjectNode>> ObjectsAsync(DbConnection connection, string database, string type, string kind, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT TOP {SqlObjectExplorer.MaxChildren} u.name, o.name, o.id
            FROM {Quote(database)}..sysobjects o JOIN {Quote(database)}..sysusers u ON u.uid = o.uid
            WHERE o.type = @type
            ORDER BY u.name, o.name
            """,
            [("@type", type)],
            r => new SqlObjectNode
            {
                Kind = kind,
                Label = $"{DbQuery.Text(r, 0)}.{DbQuery.Text(r, 1)}",
                Database = database,
                Schema = DbQuery.Text(r, 0),
                Name = DbQuery.Text(r, 1),
                ObjectId = DbQuery.Int(r, 2),
                Expandable = true,
            },
            cancellationToken);

    private sealed record AseColumn(string Name, string Type, int Length, int? Precision, int? Scale, bool Nullable, bool Identity, bool Output);

    private static async Task<IReadOnlyList<AseColumn>> ReadColumnsAsync(DbConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT c.name, t.name, c.length, c.prec, c.scale, c.status, c.status2
            FROM {Quote(database)}..syscolumns c
            JOIN {Quote(database)}..systypes t ON t.usertype = c.usertype
            WHERE c.id = @id
            ORDER BY c.colid
            """,
            [("@id", objectId)],
            r => new AseColumn(
                DbQuery.Text(r, 0) ?? string.Empty,
                DbQuery.Text(r, 1) ?? string.Empty,
                DbQuery.Int(r, 2),
                r.IsDBNull(3) ? null : DbQuery.Int(r, 3),
                r.IsDBNull(4) ? null : DbQuery.Int(r, 4),
                (DbQuery.Int(r, 5) & 8) != 0,
                (DbQuery.Int(r, 5) & 128) != 0,
                (DbQuery.Int(r, 6) & 2) != 0),
            cancellationToken);

    /// <summary>A column's type as it's declared, e.g. varchar(30) or numeric(10,2).</summary>
    private static string TypeName(AseColumn column) => column.Type switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{column.Type}({column.Length})",
        "nchar" or "nvarchar" => $"{column.Type}({column.Length / 3})",
        "unichar" or "univarchar" => $"{column.Type}({column.Length / 2})",
        "numeric" or "decimal" or "numericn" or "decimaln" => $"{column.Type.TrimEnd('n')}({column.Precision},{column.Scale})",
        "intn" => column.Length switch { 1 => "tinyint", 2 => "smallint", 8 => "bigint", _ => "int" },
        "floatn" => column.Length == 4 ? "real" : "float",
        "datetimn" => column.Length == 4 ? "smalldatetime" : "datetime",
        "moneyn" => column.Length == 4 ? "smallmoney" : "money",
        _ => column.Type,
    };

    private static async Task<IReadOnlyList<SqlObjectNode>> ColumnsAsync(DbConnection connection, string database, int objectId, bool parameters, CancellationToken cancellationToken) =>
        [.. (await ReadColumnsAsync(connection, database, objectId, cancellationToken)).Select(c => new SqlObjectNode
        {
            Kind = parameters ? Kinds.Parameter : Kinds.Column,
            Label = c.Name,
            Detail = parameters
                ? $"({TypeName(c)}, {(c.Output ? "output" : "input")})"
                : $"({TypeName(c)}{(c.Identity ? ", identity" : "")}, {(c.Nullable ? "null" : "not null")})",
        })];

    private static async Task<IReadOnlyList<SqlObjectNode>> KeysAsync(DbConnection connection, string database, int objectId, CancellationToken cancellationToken)
    {
        var keys = await DbQuery.ListAsync(
            connection,
            $"SELECT name, status FROM {Quote(database)}..sysindexes WHERE id = @id AND indid > 0 AND indid < 255 AND (status & 2048 <> 0 OR status & 4096 <> 0) ORDER BY name",
            [("@id", objectId)],
            r => new SqlObjectNode { Kind = Kinds.Key, Label = DbQuery.Text(r, 0)!, Detail = (DbQuery.Int(r, 1) & 2048) != 0 ? "Primary key" : "Unique" },
            cancellationToken);
        var foreign = await DbQuery.ListAsync(
            connection,
            $"SELECT o.name FROM {Quote(database)}..sysreferences f JOIN {Quote(database)}..sysobjects o ON o.id = f.constrid WHERE f.tableid = @id ORDER BY o.name",
            [("@id", objectId)],
            r => new SqlObjectNode { Kind = Kinds.Key, Label = DbQuery.Text(r, 0)!, Detail = "Foreign key" },
            cancellationToken);
        return [.. keys, .. foreign];
    }

    // ---- Scripts ----

    public async Task<string> ScriptAsync(DbConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken)
    {
        var offered = action switch
        {
            SqlScripter.SelectTop => node.Kind is Kinds.Table or Kinds.View,
            SqlScripter.Create => node.Kind is Kinds.Table or Kinds.View or Kinds.Procedure or Kinds.Function or Kinds.Trigger,
            SqlScripter.Execute => node.Kind is Kinds.Procedure,
            _ => false,
        };
        if (!offered || node.ObjectId is not { } objectId || node.Name is null)
        {
            throw new SqlExplorerException("That script isn't offered for this object.");
        }

        var database = Required(node.Database);
        var name = $"{Quote(node.Schema ?? "dbo")}.{Quote(node.Name)}";
        var use = $"USE {Quote(database)}\ngo\n\n";
        switch (action)
        {
            case SqlScripter.SelectTop:
                return $"{use}SELECT TOP 1000 *\nFROM {name}\ngo\n";
            case SqlScripter.Execute:
                var parameters = await ReadColumnsAsync(connection, database, objectId, cancellationToken);
                return parameters.Count == 0
                    ? $"{use}EXEC {name}\ngo\n"
                    : $"{use}EXEC {name}\n{string.Join(",\n", parameters.Select(p => $"    {p.Name} = NULL{(p.Output ? " OUTPUT" : "")}"))}\ngo\n";
            default:
                if (node.Kind == Kinds.Table)
                {
                    var columns = await ReadColumnsAsync(connection, database, objectId, cancellationToken);
                    var lines = columns.Select(c => $"    {Quote(c.Name)} {TypeName(c)}{(c.Identity ? " IDENTITY" : "")} {(c.Nullable ? "NULL" : "NOT NULL")}");
                    return $"{use}CREATE TABLE {name} (\n{string.Join(",\n", lines)}\n)\ngo\n";
                }

                var text = await DbQuery.ListAsync(
                    connection,
                    $"SELECT text FROM {Quote(database)}..syscomments WHERE id = @id ORDER BY colid2, colid",
                    [("@id", objectId)],
                    r => DbQuery.Text(r, 0) is null ? string.Empty : r.GetString(0),
                    cancellationToken);
                if (text.Count == 0)
                {
                    throw new SqlExplorerException("Its source text isn't available (it may be hidden with sp_hidetext).");
                }

                var source = new StringBuilder();
                foreach (var part in text)
                {
                    source.Append(part);
                }

                return $"{use}{source.ToString().Trim()}\ngo\n";
        }
    }

    // ---- Blocking ----

    public async Task<BlockingReport> BlockingAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await DbQuery.ListAsync(
            connection,
            """
            SELECT p.spid, suser_name(p.suid), p.hostname, p.program_name, db_name(p.dbid), p.status,
                   CASE WHEN p.tran_name IS NULL OR p.tran_name = '' THEN 0 ELSE 1 END,
                   p.blocked, p.cmd, p.time_blocked
            FROM master..sysprocesses p
            WHERE p.blocked > 0 OR p.spid IN (SELECT blocked FROM master..sysprocesses WHERE blocked > 0)
            """,
            [],
            r => new BlockingSession(
                DbQuery.Int(r, 0),
                DbQuery.Text(r, 1),
                DbQuery.Text(r, 2),
                DbQuery.Text(r, 3),
                DbQuery.Text(r, 4),
                DbQuery.Text(r, 5),
                DbQuery.Int(r, 6),
                DbQuery.Int(r, 7) > 0 ? DbQuery.Int(r, 7) : null,
                DbQuery.Text(r, 8),
                DbQuery.Int(r, 7) > 0 ? "blocked" : null,
                DbQuery.Int(r, 7) > 0 ? DbQuery.Long(r, 9) * 1000 : null,
                null,
                null,
                0,
                0,
                []),
            cancellationToken);

        if (rows.Count > 0)
        {
            rows = await WithSqlTextAsync(connection, rows, cancellationToken);
        }

        return new BlockingReport(SqlBlocking.BuildChains(rows), rows.Count(r => r.BlockedBy is not null), true, now);
    }

    /// <summary>The SQL each session runs, from monProcessSQLText (it needs mon_role and monitoring enabled; without, none).</summary>
    private static async Task<IReadOnlyList<BlockingSession>> WithSqlTextAsync(DbConnection connection, IReadOnlyList<BlockingSession> rows, CancellationToken cancellationToken)
    {
        try
        {
            var ids = string.Join(",", rows.Select(r => r.SessionId.ToString(CultureInfo.InvariantCulture)));
            var parts = await DbQuery.ListAsync(
                connection,
                $"SELECT SPID, SQLText FROM master..monProcessSQLText WHERE SPID IN ({ids}) ORDER BY SPID, LineNumber, SequenceInLine",
                [],
                r => (Spid: DbQuery.Int(r, 0), Text: DbQuery.Text(r, 1) is null ? string.Empty : r.GetString(1)),
                cancellationToken);
            var text = parts.GroupBy(p => p.Spid).ToDictionary(g => g.Key, g => string.Concat(g.Select(p => p.Text)));
            return [.. rows.Select(r => text.TryGetValue(r.SessionId, out var sql) ? r with { Sql = sql.Length > SqlBlocking.MaxSqlChars ? sql[..SqlBlocking.MaxSqlChars] : sql } : r)];
        }
        catch (AseException)
        {
            return rows;
        }
    }

    public Task KillAsync(DbConnection connection, int sessionId, int? serial, CancellationToken cancellationToken) =>
        DbQuery.ExecuteAsync(connection, $"kill {sessionId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
}
