using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Oracle;

/// <summary>
/// Oracle's query runner (statements split as SQL*Plus does, DBMS_OUTPUT under Messages), object
/// explorer (schemas through the ALL_ views, so any login sees what it may use), scripts (DDL from
/// DBMS_METADATA) and blocking manager (V$SESSION; needs SELECT_CATALOG_ROLE or similar).
/// The "database" a window is in is its current schema.
/// </summary>
internal sealed partial class OracleDialect : IDatabaseDialect
{
    public static readonly OracleDialect Instance = new();

    /// <summary>Messages from DBMS_OUTPUT kept per statement.</summary>
    private const int MaxOutputLines = 10_000;

    private const string Packages = "packages";
    private const string Package = "package";
    private const string Sequences = "sequences";
    private const string Sequence = "sequence";

    /// <summary><c>"NAME"</c>, with <c>"</c> doubled: safe to put in SQL as an identifier.</summary>
    public static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static Task SetSchemaAsync(DbConnection connection, string schema, CancellationToken cancellationToken) =>
        DbQuery.ExecuteAsync(connection, $"ALTER SESSION SET CURRENT_SCHEMA = {Quote(schema)}", cancellationToken);

    public async Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await DbQuery.ListAsync(connection, "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual", [], r => DbQuery.Text(r, 0) ?? string.Empty, cancellationToken))[0];

    public async Task<IReadOnlyList<string>> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(connection, "SELECT username FROM all_users ORDER BY username", [], r => DbQuery.Text(r, 0)!, cancellationToken);

    public async Task<string> ChangeDatabaseAsync(DbConnection connection, string name, CancellationToken cancellationToken)
    {
        await SetSchemaAsync(connection, name, cancellationToken);
        return await CurrentDatabaseAsync(connection, cancellationToken);
    }

    // ---- Running scripts ----

    public async Task<SqlQueryResult> RunAsync(DbConnection connection, string script, int lineOffset, CancellationToken cancellationToken)
    {
        var oracle = (OracleConnection)connection;
        var output = new List<SqlOutput>();
        var sent = 0;
        var cancelled = false;
        var hasErrors = false;
        var watch = Stopwatch.StartNew();

        foreach (var statement in OracleScript.Split(script))
        {
            var line = statement.StartLine + lineOffset;
            await using var command = oracle.CreateCommand();
            command.CommandText = statement.Text;
            command.CommandTimeout = 0;
            command.BindByName = true;
            // LONG columns (e.g. ALL_VIEWS.TEXT) are read whole; LOBs are read up to the value limit.
            command.InitialLONGFetchSize = -1;
            await using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch (Exception ex) when (ex is OracleException or InvalidOperationException or ObjectDisposedException)
                {
                }
            });

            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (reader.FieldCount > 0)
                {
                    var result = await ReadResultAsync((OracleDataReader)reader, SqlQueryRunner.MaxRowsTotal - sent, cancellationToken);
                    sent += result.Rows!.Count;
                    output.Add(result);
                    output.Add(new SqlOutput("rows", $"({result.RowCount} {(result.RowCount == 1 ? "row" : "rows")} selected)", RowCount: result.RowCount));
                }
                else
                {
                    output.Add(Completed(statement, reader.RecordsAffected));
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or OracleException)
            {
                cancelled = true;
                output.Add(new SqlOutput("error", "Query was cancelled by user."));
                break;
            }
            catch (OracleException ex) when (ex.Number == 24344)
            {
                // Created, with compilation errors: list them, as SQL*Plus's SHOW ERRORS does.
                hasErrors = true;
                output.Add(new SqlOutput("error", ex.Message, Number: ex.Number, Line: line));
                output.AddRange(await CompilationErrorsAsync(oracle, statement.Text, cancellationToken));
            }
            catch (OracleException ex)
            {
                hasErrors = true;
                output.Add(new SqlOutput("error", ex.Message, Number: ex.Number, Line: line));
                if (connection.State != ConnectionState.Open || ex.Number is 28 or 3113 or 3114 or 3135 or 12571)
                {
                    return new SqlQueryResult(output, string.Empty, watch.ElapsedMilliseconds, false, true, true);
                }
            }

            output.AddRange(await DbmsOutputAsync(oracle, cancellationToken));
        }

        string schema;
        try
        {
            schema = await CurrentDatabaseAsync(connection, CancellationToken.None);
        }
        catch (OracleException)
        {
            schema = string.Empty;
        }

        return new SqlQueryResult(output, schema, watch.ElapsedMilliseconds, cancelled, connection.State != ConnectionState.Open, hasErrors);
    }

    /// <summary>What SQL*Plus says after a statement that returns no rows.</summary>
    private static SqlOutput Completed(OracleStatement statement, int affected)
    {
        if (statement.PlSql && !CreateStart().IsMatch(statement.Text.TrimStart()))
        {
            return new SqlOutput("message", "PL/SQL procedure successfully completed.");
        }

        var verb = FirstWord().Match(statement.Text.TrimStart()).Value.ToUpperInvariant();
        return verb switch
        {
            "INSERT" or "UPDATE" or "DELETE" or "MERGE" => new SqlOutput("rows", $"({affected} {(affected == 1 ? "row" : "rows")} affected)", RowCount: affected),
            "CREATE" => new SqlOutput("message", "Created."),
            "ALTER" => new SqlOutput("message", "Altered."),
            "DROP" => new SqlOutput("message", "Dropped."),
            "TRUNCATE" => new SqlOutput("message", "Table truncated."),
            "GRANT" => new SqlOutput("message", "Grant succeeded."),
            "REVOKE" => new SqlOutput("message", "Revoke succeeded."),
            "COMMIT" => new SqlOutput("message", "Commit complete."),
            "ROLLBACK" => new SqlOutput("message", "Rollback complete."),
            "COMMENT" => new SqlOutput("message", "Comment created."),
            _ => new SqlOutput("message", "Statement processed."),
        };
    }

    private static async Task<IReadOnlyList<SqlOutput>> DbmsOutputAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        var lines = new List<SqlOutput>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN DBMS_OUTPUT.GET_LINE(:line, :status); END;";
            command.BindByName = true;
            var text = command.Parameters.Add("line", OracleDbType.Varchar2, 32767, null, ParameterDirection.Output);
            var status = command.Parameters.Add("status", OracleDbType.Int32, ParameterDirection.Output);
            while (lines.Count < MaxOutputLines)
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                if (((OracleDecimal)status.Value).ToInt32() != 0)
                {
                    break;
                }

                var value = (OracleString)text.Value;
                lines.Add(new SqlOutput("message", value.IsNull ? string.Empty : value.Value));
            }
        }
        catch (OracleException)
        {
            // No DBMS_OUTPUT (or the connection went): nothing to add.
        }

        return lines;
    }

    private static async Task<IReadOnlyList<SqlOutput>> CompilationErrorsAsync(OracleConnection connection, string statement, CancellationToken cancellationToken)
    {
        var match = CreatedName().Match(statement);
        if (!match.Success)
        {
            return [];
        }

        var name = match.Groups["name"].Value;
        name = name.StartsWith('"') ? name.Trim('"') : name.ToUpperInvariant();
        var owner = match.Groups["owner"].Success ? match.Groups["owner"].Value : null;
        owner = owner is null ? null : owner.StartsWith('"') ? owner.Trim('"') : owner.ToUpperInvariant();
        try
        {
            return await DbQuery.ListAsync(
                connection,
                "SELECT type, line, position, text FROM all_errors WHERE owner = NVL(:owner, SYS_CONTEXT('USERENV','CURRENT_SCHEMA')) AND name = :name ORDER BY type, sequence",
                [("owner", owner), ("name", name)],
                r => new SqlOutput("error", $"{DbQuery.Text(r, 0)} {name}, line {DbQuery.Int(r, 1)}, column {DbQuery.Int(r, 2)}: {DbQuery.Text(r, 3)}", Line: DbQuery.Int(r, 1)),
                cancellationToken);
        }
        catch (OracleException)
        {
            return [];
        }
    }

    internal static async Task<SqlOutput> ReadResultAsync(OracleDataReader reader, int budget, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new SqlColumn(reader.GetName(i), reader.GetDataTypeName(i).ToUpperInvariant(), DbValueText.IsNumeric(reader.GetFieldType(i))))
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
                    row[i] = Value(reader, i, SqlQueryRunner.MaxCharsPerValue);
                }

                rows.Add(row);
            }
        }

        return new SqlOutput("results", Columns: columns, Rows: rows, RowCount: count, Truncated: count > rows.Count);
    }

    /// <summary>A value as SQL Developer shows it: NUMBERs in full, dates as yyyy-MM-dd HH:mm:ss, LOBs up to the limit.</summary>
    private static string? Value(OracleDataReader reader, int ordinal, int maxChars)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            var text = reader.GetProviderSpecificValue(ordinal) switch
            {
                OracleDecimal d => d.ToString(),
                OracleDate d => d.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                OracleTimeStamp t => t.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                OracleTimeStampLTZ t => t.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                OracleTimeStampTZ t => $"{t.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)} {t.TimeZone}",
                OracleClob clob => ReadClob(clob, maxChars),
                OracleBlob blob => DbValueText.Hex(ReadBlob(blob, maxChars), maxChars),
                OracleBFile => "(BFILE)",
                OracleBinary binary => DbValueText.Hex(binary.Value, maxChars),
                OracleString s => s.Value,
                var other => DbValueText.Format(reader.GetValue(ordinal) ?? other, maxChars),
            };
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        catch (Exception ex) when (ex is InvalidCastException or OracleException or NotSupportedException or OverflowException)
        {
            // Object types (XMLTYPE, SDO_GEOMETRY…) aren't read as values here.
            return $"({reader.GetDataTypeName(ordinal)})";
        }
    }

    private static string ReadClob(OracleClob clob, int maxChars)
    {
        var buffer = new char[Math.Min(maxChars, Math.Max(0, (int)Math.Min(int.MaxValue, clob.Length / 2)) + 1)];
        var read = clob.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, Math.Max(0, read));
    }

    private static byte[] ReadBlob(OracleBlob blob, int maxChars)
    {
        var buffer = new byte[(int)Math.Min(blob.Length, (maxChars - 2) / 2)];
        var read = blob.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    // ---- Object explorer ----

    public async Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken)
    {
        var schema = node.Database;
        return node.Kind switch
        {
            Kinds.Server =>
            [
                Folder(Kinds.Databases, "Schemas"),
                Folder(Kinds.ServerSecurity, "Security"),
                Folder(Kinds.Management, "Management"),
            ],
            Kinds.Databases => [Folder(Kinds.SystemDatabases, "Oracle-maintained Schemas"), .. await SchemasAsync(connection, system: false, cancellationToken)],
            Kinds.SystemDatabases => await SchemasAsync(connection, system: true, cancellationToken),
            Kinds.Database =>
            [
                Folder(Kinds.Tables, "Tables", schema),
                Folder(Kinds.Views, "Views", schema),
                Folder(Kinds.Synonyms, "Synonyms", schema),
                Folder(Sequences, "Sequences", schema),
                Folder(Kinds.Programmability, "Programmability", schema),
            ],
            Kinds.Tables => await ListAsync(
                connection,
                "SELECT table_name FROM all_tables WHERE owner = :owner AND nested = 'NO' AND secondary = 'N' AND table_name NOT LIKE 'BIN$%' ORDER BY table_name",
                Required(schema),
                Kinds.Table,
                expandable: true,
                cancellationToken),
            Kinds.Views => await ListAsync(connection, "SELECT view_name FROM all_views WHERE owner = :owner ORDER BY view_name", Required(schema), Kinds.View, expandable: true, cancellationToken),
            Kinds.Table => ObjectFolders(node, Kinds.Columns, Kinds.Keys, Kinds.Indexes, Kinds.Triggers),
            Kinds.View => ObjectFolders(node, Kinds.Columns),
            Kinds.Synonyms => await DbQuery.ListAsync(
                connection,
                $"SELECT synonym_name, table_owner, table_name, db_link FROM all_synonyms WHERE owner = :owner AND ROWNUM <= {SqlObjectExplorer.MaxChildren} ORDER BY synonym_name",
                [("owner", Required(schema))],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Synonym,
                    Label = DbQuery.Text(r, 0)!,
                    Detail = $"{DbQuery.Text(r, 1)}.{DbQuery.Text(r, 2)}{(DbQuery.Text(r, 3) is { } link ? "@" + link : "")}",
                    Database = schema,
                    Schema = schema,
                    Name = DbQuery.Text(r, 0),
                },
                cancellationToken),
            Sequences => await ListAsync(connection, "SELECT sequence_name FROM all_sequences WHERE sequence_owner = :owner ORDER BY sequence_name", Required(schema), Sequence, expandable: false, cancellationToken),
            Kinds.Programmability => [Folder(Kinds.Procedures, "Procedures", schema), Folder(Kinds.Functions, "Functions", schema), Folder(Packages, "Packages", schema)],
            Kinds.Procedures => await ProgramsAsync(connection, Required(schema), "PROCEDURE", Kinds.Procedure, cancellationToken),
            Kinds.Functions => await ProgramsAsync(connection, Required(schema), "FUNCTION", Kinds.Function, cancellationToken),
            Packages => await ProgramsAsync(connection, Required(schema), "PACKAGE", Package, cancellationToken),
            Kinds.Procedure or Kinds.Function => await ArgumentsAsync(connection, node, cancellationToken),
            Kinds.Columns => await ColumnsAsync(connection, node, cancellationToken),
            Kinds.Keys => await DbQuery.ListAsync(
                connection,
                "SELECT constraint_name, constraint_type, status FROM all_constraints WHERE owner = :owner AND table_name = :name AND constraint_type IN ('P','U','R') ORDER BY constraint_type, constraint_name",
                [("owner", Required(schema)), ("name", RequiredName(node))],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Key,
                    Label = DbQuery.Text(r, 0)!,
                    Detail = (DbQuery.Text(r, 1) switch { "P" => "Primary key", "U" => "Unique", _ => "Foreign key" }) + (DbQuery.Text(r, 2) == "DISABLED" ? ", disabled" : ""),
                },
                cancellationToken),
            Kinds.Indexes => await DbQuery.ListAsync(
                connection,
                "SELECT owner, index_name, uniqueness, index_type, status FROM all_indexes WHERE table_owner = :owner AND table_name = :name ORDER BY index_name",
                [("owner", Required(schema)), ("name", RequiredName(node))],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Index,
                    Label = DbQuery.Text(r, 1)!,
                    Detail = $"{(DbQuery.Text(r, 2) == "UNIQUE" ? "Unique, " : "")}{DbQuery.Text(r, 3)?.ToLowerInvariant()}{(DbQuery.Text(r, 4) == "UNUSABLE" ? ", unusable" : "")}",
                },
                cancellationToken),
            Kinds.Triggers => await DbQuery.ListAsync(
                connection,
                "SELECT owner, trigger_name, trigger_type, status FROM all_triggers WHERE table_owner = :owner AND table_name = :name ORDER BY trigger_name",
                [("owner", Required(schema)), ("name", RequiredName(node))],
                r => new SqlObjectNode
                {
                    Kind = Kinds.Trigger,
                    Label = DbQuery.Text(r, 1)!,
                    Detail = $"{DbQuery.Text(r, 2)?.ToLowerInvariant()}{(DbQuery.Text(r, 3) == "DISABLED" ? ", disabled" : "")}",
                    Database = schema,
                    Schema = DbQuery.Text(r, 0),
                    Name = DbQuery.Text(r, 1),
                },
                cancellationToken),
            Kinds.ServerSecurity => [Folder(Kinds.Logins, "Users"), Folder(Kinds.ServerRoles, "Roles")],
            Kinds.Logins => await DbQuery.ListAsync(
                connection,
                "SELECT username, created FROM all_users ORDER BY username",
                [],
                r => new SqlObjectNode { Kind = Kinds.Login, Label = DbQuery.Text(r, 0)!, Detail = r.IsDBNull(1) ? null : $"created {r.GetDateTime(1):yyyy-MM-dd}" },
                cancellationToken),
            Kinds.ServerRoles => await RolesAsync(connection, cancellationToken),
            Kinds.Management =>
            [
                new SqlObjectNode { Kind = Kinds.Tool, Label = "Blocking Manager", Detail = "Blocking chains", Name = SqlObjectExplorer.BlockingManagerTool },
                .. OracleDba.ToolNodes,
            ],
            _ => throw new SqlExplorerException($"“{node.Kind}” has no children."),
        };
    }

    private static string Required(string? schema) => string.IsNullOrEmpty(schema) ? throw new SqlExplorerException("Choose a schema.") : schema;

    private static string RequiredName(SqlObjectNode node) => node.Name ?? throw new SqlExplorerException("Choose an object.");

    private static SqlObjectNode Folder(string kind, string label, string? schema = null) => new() { Kind = kind, Label = label, Expandable = true, Database = schema };

    private static IReadOnlyList<SqlObjectNode> ObjectFolders(SqlObjectNode parent, params string[] kinds) =>
        [.. kinds.Select(kind => parent with
        {
            Kind = kind,
            Label = kind switch { Kinds.Columns => "Columns", Kinds.Keys => "Constraints", Kinds.Indexes => "Indexes", _ => "Triggers" },
            Detail = null,
            Expandable = true,
        })];

    private static async Task<IReadOnlyList<SqlObjectNode>> SchemasAsync(DbConnection connection, bool system, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, bool System)> schemas;
        try
        {
            // ORACLE_MAINTAINED is 12c and later.
            schemas = await DbQuery.ListAsync(connection, "SELECT username, oracle_maintained FROM all_users ORDER BY username", [], r => (DbQuery.Text(r, 0)!, DbQuery.Text(r, 1) == "Y"), cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == 904)
        {
            schemas = await DbQuery.ListAsync(connection, "SELECT username FROM all_users ORDER BY username", [], r => (DbQuery.Text(r, 0)!, Oracle11Schemas.Contains(DbQuery.Text(r, 0)!)), cancellationToken);
        }

        return [.. schemas.Where(s => s.System == system).Take(SqlObjectExplorer.MaxChildren).Select(s => new SqlObjectNode
        {
            Kind = Kinds.Database,
            Label = s.Name,
            Database = s.Name,
            Name = s.Name,
            Expandable = true,
        })];
    }

    /// <summary>The schemas Oracle 11g installs (it has no ORACLE_MAINTAINED column to tell them apart).</summary>
    private static readonly HashSet<string> Oracle11Schemas = new(StringComparer.Ordinal)
    {
        "ANONYMOUS", "APEX_030200", "APEX_040000", "APEX_PUBLIC_USER", "APPQOSSYS", "CTXSYS", "DBSNMP", "DIP", "EXFSYS", "FLOWS_FILES", "MDDATA", "MDSYS",
        "MGMT_VIEW", "OLAPSYS", "ORACLE_OCM", "ORDDATA", "ORDPLUGINS", "ORDSYS", "OUTLN", "OWBSYS", "OWBSYS_AUDIT", "SI_INFORMTN_SCHEMA", "SPATIAL_CSW_ADMIN_USR",
        "SPATIAL_WFS_ADMIN_USR", "SYS", "SYSMAN", "SYSTEM", "WMSYS", "XDB", "XS$NULL",
    };

    private static async Task<IReadOnlyList<SqlObjectNode>> ListAsync(
        DbConnection connection, string sql, string schema, string kind, bool expandable, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"SELECT * FROM ({sql}) WHERE ROWNUM <= {SqlObjectExplorer.MaxChildren}",
            [("owner", schema)],
            r => new SqlObjectNode { Kind = kind, Label = DbQuery.Text(r, 0)!, Database = schema, Schema = schema, Name = DbQuery.Text(r, 0), Expandable = expandable },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> ProgramsAsync(DbConnection connection, string schema, string type, string kind, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"SELECT object_name, status FROM all_objects WHERE owner = :owner AND object_type = :type AND ROWNUM <= {SqlObjectExplorer.MaxChildren} ORDER BY object_name",
            [("owner", schema), ("type", type)],
            r => new SqlObjectNode
            {
                Kind = kind,
                Label = DbQuery.Text(r, 0)!,
                Detail = DbQuery.Text(r, 1) == "INVALID" ? "invalid" : null,
                Database = schema,
                Schema = schema,
                Name = DbQuery.Text(r, 0),
                Expandable = kind != Package,
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> ColumnsAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            """
            SELECT column_name, data_type, data_length, data_precision, data_scale, nullable, char_length, char_used
            FROM all_tab_columns WHERE owner = :owner AND table_name = :name ORDER BY column_id
            """,
            [("owner", Required(node.Database)), ("name", RequiredName(node))],
            r => new SqlObjectNode
            {
                Kind = Kinds.Column,
                Label = DbQuery.Text(r, 0)!,
                Detail = $"({TypeName(DbQuery.Text(r, 1)!, DbQuery.Long(r, 2), DbQuery.Long(r, 3), DbQuery.Long(r, 4), DbQuery.Long(r, 6), DbQuery.Text(r, 7))}, {(DbQuery.Text(r, 5) == "N" ? "not null" : "null")})",
            },
            cancellationToken);

    /// <summary>A column's type as it's declared, e.g. VARCHAR2(30 CHAR) or NUMBER(10,2).</summary>
    internal static string TypeName(string type, long? length, long? precision, long? scale, long? charLength, string? charUsed) => type switch
    {
        "VARCHAR2" or "CHAR" => $"{type}({charLength ?? length}{(charUsed == "C" ? " CHAR" : "")})",
        "NVARCHAR2" or "NCHAR" => $"{type}({charLength ?? length})",
        "RAW" => $"RAW({length})",
        "NUMBER" when precision is not null => scale is null or 0 ? $"NUMBER({precision})" : $"NUMBER({precision},{scale})",
        "NUMBER" when scale == 0 => "INTEGER",
        "FLOAT" when precision is not null => $"FLOAT({precision})",
        _ => type,
    };

    private static async Task<IReadOnlyList<SqlObjectNode>> ArgumentsAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            """
            SELECT argument_name, data_type, in_out, position FROM all_arguments
            WHERE owner = :owner AND object_name = :name AND package_name IS NULL AND data_level = 0
            ORDER BY position
            """,
            [("owner", Required(node.Database)), ("name", RequiredName(node))],
            r => new SqlObjectNode
            {
                Kind = Kinds.Parameter,
                Label = DbQuery.Text(r, 0) ?? (DbQuery.Int(r, 3) == 0 ? "Returns" : "(unnamed)"),
                Detail = DbQuery.Int(r, 3) == 0 ? $"({DbQuery.Text(r, 1)})" : $"({DbQuery.Text(r, 1)}, {DbQuery.Text(r, 2)?.ToLowerInvariant().Replace('/', ' ')})",
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> RolesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await DbQuery.ListAsync(connection, "SELECT role FROM dba_roles ORDER BY role", [], r => new SqlObjectNode { Kind = Kinds.ServerRole, Label = DbQuery.Text(r, 0)! }, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == 942)
        {
            // Without access to DBA_ROLES: the roles this session has.
            return await DbQuery.ListAsync(
                connection, "SELECT role FROM session_roles ORDER BY role", [], r => new SqlObjectNode { Kind = Kinds.ServerRole, Label = DbQuery.Text(r, 0)!, Detail = "yours" }, cancellationToken);
        }
    }

    // ---- Scripts ----

    public async Task<string> ScriptAsync(DbConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken)
    {
        var offered = action switch
        {
            SqlScripter.SelectTop => node.Kind is Kinds.Table or Kinds.View or Kinds.Synonym,
            SqlScripter.Create => node.Kind is Kinds.Table or Kinds.View or Kinds.Procedure or Kinds.Function or Kinds.Trigger or Package or Sequence or Kinds.Synonym,
            SqlScripter.Execute => node.Kind is Kinds.Procedure,
            _ => false,
        };
        if (!offered || node.Name is null)
        {
            throw new SqlExplorerException("That script isn't offered for this object.");
        }

        var owner = node.Schema ?? Required(node.Database);
        var name = $"{Quote(owner)}.{Quote(node.Name)}";
        switch (action)
        {
            case SqlScripter.SelectTop:
                // ROWNUM works on every version (FETCH FIRST is 12c and later).
                return $"SELECT *\nFROM {name}\nWHERE ROWNUM <= 1000;\n";
            case SqlScripter.Execute:
                var arguments = await DbQuery.ListAsync(
                    connection,
                    "SELECT argument_name FROM all_arguments WHERE owner = :owner AND object_name = :name AND package_name IS NULL AND data_level = 0 AND position > 0 ORDER BY position",
                    [("owner", owner), ("name", node.Name)],
                    r => DbQuery.Text(r, 0),
                    cancellationToken);
                var list = string.Join(",\n", arguments.Where(a => a is not null).Select(a => $"    {a!.ToLowerInvariant()} => NULL"));
                return arguments.Count == 0 ? $"BEGIN\n  {name};\nEND;\n/\n" : $"BEGIN\n  {name}(\n{list}\n  );\nEND;\n/\n";
            default:
                var type = node.Kind switch
                {
                    Kinds.Table => "TABLE",
                    Kinds.View => "VIEW",
                    Kinds.Procedure => "PROCEDURE",
                    Kinds.Function => "FUNCTION",
                    Kinds.Trigger => "TRIGGER",
                    Kinds.Synonym => "SYNONYM",
                    Sequence => "SEQUENCE",
                    _ => "PACKAGE",
                };
                await DbQuery.ExecuteAsync(
                    connection,
                    """
                    BEGIN
                      DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SEGMENT_ATTRIBUTES', FALSE);
                      DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'PRETTY', TRUE);
                    END;
                    """,
                    cancellationToken);
                var ddls = await DbQuery.ListAsync(
                    connection,
                    "SELECT DBMS_METADATA.GET_DDL(:type, :name, :owner) FROM dual",
                    [("type", type), ("name", node.Name), ("owner", owner)],
                    r => r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    cancellationToken);
                var body = ddls.Count > 0 ? ddls[0].Trim() : string.Empty;
                var plSql = type is "PROCEDURE" or "FUNCTION" or "TRIGGER" or "PACKAGE";
                return plSql ? body + "\n/\n" : body + ";\n";
        }
    }

    // ---- Blocking ----

    private const string BlockingQuery =
        """
        SELECT s.sid, s.serial#, s.username, s.machine, s.program, s.schemaname, s.status,
               (SELECT COUNT(*) FROM v$transaction t WHERE t.ses_addr = s.saddr),
               s.blocking_session, s.command, s.event, s.wait_time_micro / 1000, s.row_wait_obj#,
               (SELECT MAX(q.sql_text) FROM v$sql q WHERE q.sql_id = NVL(s.sql_id, s.prev_sql_id))
        FROM v$session s
        WHERE s.blocking_session IS NOT NULL
           OR s.sid IN (SELECT blocking_session FROM v$session WHERE blocking_session IS NOT NULL)
        """;

    public async Task<BlockingReport> BlockingAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<BlockingSession> rows;
        try
        {
            rows = await DbQuery.ListAsync(
                connection,
                BlockingQuery,
                [],
                r => new BlockingSession(
                    DbQuery.Int(r, 0),
                    DbQuery.Text(r, 2),
                    DbQuery.Text(r, 3),
                    DbQuery.Text(r, 4),
                    DbQuery.Text(r, 5),
                    DbQuery.Text(r, 6)?.ToLowerInvariant(),
                    DbQuery.Int(r, 7),
                    r.IsDBNull(8) ? null : DbQuery.Int(r, 8),
                    null,
                    r.IsDBNull(8) ? null : DbQuery.Text(r, 10),
                    DbQuery.Long(r, 11),
                    r.IsDBNull(12) || DbQuery.Int(r, 12) <= 0 ? null : $"object {DbQuery.Int(r, 12)}",
                    DbQuery.Text(r, 13),
                    0,
                    0,
                    [],
                    Serial: DbQuery.Int(r, 1)),
                cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == 942)
        {
            throw new SqlExplorerException("This login can't read V$SESSION. Grant it SELECT_CATALOG_ROLE (or SELECT on V_$SESSION, V_$TRANSACTION and V_$SQL) to see blocking.");
        }

        return new BlockingReport(SqlBlocking.BuildChains(rows), rows.Count(r => r.BlockedBy is not null), true, now);
    }

    /// <summary>ALTER SYSTEM KILL SESSION 'sid,serial#' IMMEDIATE: the serial makes sure it's the same session.</summary>
    public async Task KillAsync(DbConnection connection, int sessionId, int? serial, CancellationToken cancellationToken)
    {
        if (serial is not { } s)
        {
            throw new SqlExplorerException("Killing an Oracle session takes its serial number too. Refresh and try again.");
        }

        await DbQuery.ExecuteAsync(
            connection,
            $"ALTER SYSTEM KILL SESSION '{sessionId.ToString(CultureInfo.InvariantCulture)},{s.ToString(CultureInfo.InvariantCulture)}' IMMEDIATE",
            cancellationToken);
    }

    [GeneratedRegex(@"^\w+")]
    private static partial Regex FirstWord();

    [GeneratedRegex(@"^CREATE\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateStart();

    [GeneratedRegex(
        @"^\s*CREATE\s+(OR\s+REPLACE\s+)?((NON)?EDITIONABLE\s+)?(PROCEDURE|FUNCTION|PACKAGE(\s+BODY)?|TRIGGER|TYPE(\s+BODY)?)\s+((?<owner>""[^""]+""|\w+)\s*\.\s*)?(?<name>""[^""]+""|\w+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreatedName();
}
