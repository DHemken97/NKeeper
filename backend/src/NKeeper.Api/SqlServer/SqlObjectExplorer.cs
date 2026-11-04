using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// A node of the object explorer. The browser sends a node back to list its children, so a node
/// carries what identifies it: its <see cref="Kind"/>, database, and object (schema, name, ID).
/// </summary>
public sealed record SqlObjectNode
{
    [Required]
    [StringLength(32)]
    public string Kind { get; init; } = string.Empty;

    [StringLength(256)]
    public string Label { get; init; } = string.Empty;

    /// <summary>Shown dimmed beside the label, e.g. a column's type or a database's state.</summary>
    public string? Detail { get; init; }

    public bool Expandable { get; init; }

    [StringLength(128)]
    public string? Database { get; init; }

    [StringLength(128)]
    public string? Schema { get; init; }

    [StringLength(128)]
    public string? Name { get; init; }

    public int? ObjectId { get; init; }
}

/// <summary>
/// The object explorer's tree, as SSMS shows it: the server's databases (system ones in their own
/// folder), each database's tables, views, synonyms, programmability and security, a table's columns,
/// keys, indexes and triggers; the server's logins and roles; and Management (the blocking manager and
/// orphaned users).
/// Every name is passed as a parameter or quoted, never pasted into SQL as it is.
/// </summary>
internal static class SqlObjectExplorer
{
    public static class Kinds
    {
        public const string Server = "server";
        public const string Databases = "databases";
        public const string SystemDatabases = "systemDatabases";
        public const string Database = "database";
        public const string Tables = "tables";
        public const string SystemTables = "systemTables";
        public const string Table = "table";
        public const string Views = "views";
        public const string SystemViews = "systemViews";
        public const string View = "view";
        public const string Synonyms = "synonyms";
        public const string Synonym = "synonym";
        public const string Programmability = "programmability";
        public const string Procedures = "procedures";
        public const string Procedure = "procedure";
        public const string Functions = "functions";
        public const string Function = "function";
        public const string Columns = "columns";
        public const string Column = "column";
        public const string Keys = "keys";
        public const string Key = "key";
        public const string Indexes = "indexes";
        public const string Index = "index";
        public const string Triggers = "triggers";
        public const string Trigger = "trigger";
        public const string Parameter = "parameter";
        public const string DatabaseSecurity = "databaseSecurity";
        public const string Users = "users";
        public const string User = "user";
        public const string Roles = "roles";
        public const string Role = "role";
        public const string Schemas = "schemas";
        public const string SchemaNode = "schema";
        public const string ServerSecurity = "serverSecurity";
        public const string Logins = "logins";
        public const string Login = "login";
        public const string ServerRoles = "serverRoles";
        public const string ServerRole = "serverRole";
        public const string Management = "management";

        /// <summary>A tool that opens in its own document tab (the blocking manager).</summary>
        public const string Tool = "tool";

        /// <summary>A folder of Management tools (ASE has enough to group).</summary>
        public const string ToolFolder = "toolFolder";
    }

    public const string BlockingManagerTool = "blocking";

    public const string OrphanedUsersTool = "orphans";

    /// <summary>Lists are long in big databases; the explorer shows this many children at most.</summary>
    public const int MaxChildren = 5000;

    public static async Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(SqlConnection connection, SqlObjectNode node, CancellationToken cancellationToken)
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
            Kinds.Databases => [Folder(Kinds.SystemDatabases, "System Databases"), .. await DatabasesAsync(connection, system: false, cancellationToken)],
            Kinds.SystemDatabases => await DatabasesAsync(connection, system: true, cancellationToken),
            Kinds.Database =>
            [
                Folder(Kinds.Tables, "Tables", db),
                Folder(Kinds.Views, "Views", db),
                Folder(Kinds.Synonyms, "Synonyms", db),
                Folder(Kinds.Programmability, "Programmability", db),
                Folder(Kinds.DatabaseSecurity, "Security", db),
            ],
            Kinds.Tables => [Folder(Kinds.SystemTables, "System Tables", db), .. await ObjectsAsync(connection, Required(db), "U", shipped: false, Kinds.Table, cancellationToken)],
            Kinds.SystemTables => await ObjectsAsync(connection, Required(db), "U", shipped: true, Kinds.Table, cancellationToken),
            Kinds.Views => [Folder(Kinds.SystemViews, "System Views", db), .. await ObjectsAsync(connection, Required(db), "V", shipped: false, Kinds.View, cancellationToken)],
            Kinds.SystemViews => await ObjectsAsync(connection, Required(db), "V", shipped: true, Kinds.View, cancellationToken),
            Kinds.Table => ObjectFolders(node, Kinds.Columns, Kinds.Keys, Kinds.Indexes, Kinds.Triggers),
            Kinds.View => ObjectFolders(node, Kinds.Columns, Kinds.Indexes, Kinds.Triggers),
            Kinds.Synonyms => await SynonymsAsync(connection, Required(db), cancellationToken),
            Kinds.Programmability => [Folder(Kinds.Procedures, "Stored Procedures", db), Folder(Kinds.Functions, "Functions", db)],
            Kinds.Procedures => await ObjectsAsync(connection, Required(db), "P", shipped: false, Kinds.Procedure, cancellationToken),
            Kinds.Functions => await FunctionsAsync(connection, Required(db), cancellationToken),
            Kinds.Procedure or Kinds.Function => await ParametersAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.Columns => await ColumnsAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.Keys => await KeysAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.Indexes => await IndexesAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.Triggers => await TriggersAsync(connection, Required(db), RequiredId(node), cancellationToken),
            Kinds.DatabaseSecurity => [Folder(Kinds.Users, "Users", db), Folder(Kinds.Roles, "Roles", db), Folder(Kinds.Schemas, "Schemas", db)],
            Kinds.Users => await PrincipalsAsync(connection, Required(db), roles: false, cancellationToken),
            Kinds.Roles => await PrincipalsAsync(connection, Required(db), roles: true, cancellationToken),
            Kinds.Schemas => await SchemasAsync(connection, Required(db), cancellationToken),
            Kinds.ServerSecurity => [Folder(Kinds.Logins, "Logins"), Folder(Kinds.ServerRoles, "Server Roles")],
            Kinds.Logins => await LoginsAsync(connection, roles: false, cancellationToken),
            Kinds.ServerRoles => await LoginsAsync(connection, roles: true, cancellationToken),
            Kinds.Management =>
            [
                new SqlObjectNode { Kind = Kinds.Tool, Label = "Blocking Manager", Detail = "Blocking chains", Name = BlockingManagerTool },
                new SqlObjectNode { Kind = Kinds.Tool, Label = "Orphaned Users", Detail = "Fix broken logins", Name = OrphanedUsersTool },
            ],
            _ => throw new SqlExplorerException($"“{node.Kind}” has no children."),
        };
    }

    /// <summary><c>[name]</c>, with <c>]</c> doubled: safe to put in SQL as an identifier.</summary>
    public static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    internal static string Required(string? database) =>
        string.IsNullOrEmpty(database) ? throw new SqlExplorerException("Choose a database.") : database;

    private static int RequiredId(SqlObjectNode node) => node.ObjectId ?? throw new SqlExplorerException("Choose an object.");

    private static SqlObjectNode Folder(string kind, string label, string? database = null) =>
        new() { Kind = kind, Label = label, Expandable = true, Database = database };

    private static IReadOnlyList<SqlObjectNode> ObjectFolders(SqlObjectNode parent, params string[] kinds) =>
        [.. kinds.Select(kind => parent with
        {
            Kind = kind,
            Label = kind switch { Kinds.Columns => "Columns", Kinds.Keys => "Keys", Kinds.Indexes => "Indexes", _ => "Triggers" },
            Detail = null,
            Expandable = true,
        })];

    private static async Task<IReadOnlyList<SqlObjectNode>> DatabasesAsync(SqlConnection connection, bool system, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) name, state_desc, CAST(HAS_DBACCESS(name) AS bit), is_read_only
            FROM sys.databases
            WHERE {(system ? "database_id <= 4" : "database_id > 4")}
            ORDER BY name
            """,
            [],
            r =>
            {
                var name = r.GetString(0);
                var state = r.GetString(1);
                var online = state == "ONLINE" && !r.IsDBNull(2) && r.GetBoolean(2);
                return new SqlObjectNode
                {
                    Kind = Kinds.Database,
                    Label = name,
                    Database = name,
                    Name = name,
                    Expandable = online,
                    Detail = state != "ONLINE" ? state.ToLowerInvariant().Replace('_', ' ') : !online ? "no access" : r.GetBoolean(3) ? "read-only" : null,
                };
            },
            cancellationToken);

    /// <param name="type">sys.objects type: U (tables), V (views), P (procedures, with PC).</param>
    private static async Task<IReadOnlyList<SqlObjectNode>> ObjectsAsync(
        SqlConnection connection, string database, string type, bool shipped, string kind, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) s.name, o.name, o.object_id
            FROM {Quote(database)}.sys.all_objects o
            JOIN {Quote(database)}.sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN (@type, CASE @type WHEN 'P' THEN 'PC' END) AND o.is_ms_shipped = @shipped
            ORDER BY s.name, o.name
            """,
            [new SqlParameter("@type", type), new SqlParameter("@shipped", shipped)],
            r => new SqlObjectNode
            {
                Kind = kind,
                Label = $"{r.GetString(0)}.{r.GetString(1)}",
                Database = database,
                Schema = r.GetString(0),
                Name = r.GetString(1),
                ObjectId = r.GetInt32(2),
                Expandable = true,
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> FunctionsAsync(SqlConnection connection, string database, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) s.name, o.name, o.object_id, o.type
            FROM {Quote(database)}.sys.objects o
            JOIN {Quote(database)}.sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('FN', 'IF', 'TF', 'FS', 'FT', 'AF') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name
            """,
            [],
            r => new SqlObjectNode
            {
                Kind = Kinds.Function,
                Label = $"{r.GetString(0)}.{r.GetString(1)}",
                Detail = r.GetString(3).Trim() switch { "FN" or "FS" => "Scalar-valued", "AF" => "Aggregate", _ => "Table-valued" },
                Database = database,
                Schema = r.GetString(0),
                Name = r.GetString(1),
                ObjectId = r.GetInt32(2),
                Expandable = true,
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> SynonymsAsync(SqlConnection connection, string database, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) s.name, y.name, y.object_id, y.base_object_name
            FROM {Quote(database)}.sys.synonyms y
            JOIN {Quote(database)}.sys.schemas s ON s.schema_id = y.schema_id
            ORDER BY s.name, y.name
            """,
            [],
            r => new SqlObjectNode
            {
                Kind = Kinds.Synonym,
                Label = $"{r.GetString(0)}.{r.GetString(1)}",
                Detail = r.GetString(3),
                Database = database,
                Schema = r.GetString(0),
                Name = r.GetString(1),
                ObjectId = r.GetInt32(2),
            },
            cancellationToken);

    /// <summary>Columns as SSMS labels them: <c>Id (PK, int, not null)</c>.</summary>
    private static async Task<IReadOnlyList<SqlObjectNode>> ColumnsAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT c.name, {TypeExpression(database)}, c.is_nullable, c.is_identity, c.is_computed,
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM {Quote(database)}.sys.index_columns ic
                       JOIN {Quote(database)}.sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM {Quote(database)}.sys.foreign_key_columns f
                       WHERE f.parent_object_id = c.object_id AND f.parent_column_id = c.column_id) THEN 1 ELSE 0 END AS bit)
            FROM {Quote(database)}.sys.all_columns c
            JOIN {Quote(database)}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """,
            [new SqlParameter("@id", objectId)],
            r =>
            {
                var traits = new List<string>();
                if (r.GetBoolean(5))
                {
                    traits.Add("PK");
                }

                if (r.GetBoolean(6))
                {
                    traits.Add("FK");
                }

                traits.Add(r.GetBoolean(4) ? "Computed" : r.GetString(1));
                traits.Add(r.GetBoolean(2) ? "null" : "not null");
                return new SqlObjectNode
                {
                    Kind = Kinds.Column,
                    Label = r.GetString(0),
                    Detail = string.Join(", ", traits) + (r.GetBoolean(3) ? ", identity" : ""),
                    Database = database,
                    Name = r.GetString(0),
                    ObjectId = objectId,
                };
            },
            cancellationToken);

    /// <summary>A column's or parameter's type as written in T-SQL: <c>nvarchar(50)</c>, <c>decimal(18,2)</c>, <c>varchar(max)</c>.</summary>
    internal static string TypeExpression(string database) =>
        $"""
        CASE
            WHEN t.name IN ('varchar', 'char', 'varbinary', 'binary') THEN t.name + '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
            WHEN t.name IN ('nvarchar', 'nchar') THEN t.name + '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
            WHEN t.name IN ('decimal', 'numeric') THEN t.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
            WHEN t.name IN ('datetime2', 'time', 'datetimeoffset') THEN t.name + '(' + CAST(c.scale AS varchar(10)) + ')'
            WHEN t.is_user_defined = 1 THEN t.name
            ELSE t.name
        END
        """;

    private static async Task<IReadOnlyList<SqlObjectNode>> ParametersAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT CASE c.parameter_id WHEN 0 THEN 'Returns' ELSE c.name END, {TypeExpression(database)}, c.is_output, c.has_default_value, c.parameter_id
            FROM {Quote(database)}.sys.all_parameters c
            JOIN {Quote(database)}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = @id
            ORDER BY c.parameter_id
            """,
            [new SqlParameter("@id", objectId)],
            r => new SqlObjectNode
            {
                Kind = Kinds.Parameter,
                Label = r.GetString(0),
                Detail = r.GetInt32(4) == 0
                    ? r.GetString(1)
                    : $"{r.GetString(1)}, {(r.GetBoolean(2) ? "Input/Output" : "Input")}, {(r.GetBoolean(3) ? "Default" : "No default")}",
                Database = database,
                ObjectId = objectId,
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> KeysAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT name, type_desc, object_id FROM {Quote(database)}.sys.key_constraints WHERE parent_object_id = @id
            UNION ALL
            SELECT name, type_desc, object_id FROM {Quote(database)}.sys.foreign_keys WHERE parent_object_id = @id
            ORDER BY 2 DESC, 1
            """,
            [new SqlParameter("@id", objectId)],
            r => new SqlObjectNode
            {
                Kind = Kinds.Key,
                Label = r.GetString(0),
                Detail = r.GetString(1) switch { "PRIMARY_KEY_CONSTRAINT" => "Primary key", "UNIQUE_CONSTRAINT" => "Unique", _ => "Foreign key" },
                Database = database,
                Name = r.GetString(0),
                ObjectId = r.GetInt32(2),
            },
            cancellationToken);

    /// <summary>Indexes as SSMS labels them: <c>IX_Name (Non-Unique, Non-Clustered)</c>.</summary>
    private static async Task<IReadOnlyList<SqlObjectNode>> IndexesAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT name, type_desc, is_unique, is_primary_key, is_disabled
            FROM {Quote(database)}.sys.indexes
            WHERE object_id = @id AND index_id > 0
            ORDER BY index_id
            """,
            [new SqlParameter("@id", objectId)],
            r => new SqlObjectNode
            {
                Kind = Kinds.Index,
                Label = r.GetString(0),
                Detail = $"{(r.GetBoolean(3) ? "Primary key" : r.GetBoolean(2) ? "Unique" : "Non-Unique")}, {IndexType(r.GetString(1))}{(r.GetBoolean(4) ? ", disabled" : "")}",
                Database = database,
                Name = r.GetString(0),
                ObjectId = objectId,
            },
            cancellationToken);

    private static string IndexType(string typeDesc) => typeDesc switch
    {
        "CLUSTERED" => "Clustered",
        "NONCLUSTERED" => "Non-Clustered",
        "CLUSTERED COLUMNSTORE" => "Clustered Columnstore",
        "NONCLUSTERED COLUMNSTORE" => "Non-Clustered Columnstore",
        _ => typeDesc.ToLowerInvariant(),
    };

    private static async Task<IReadOnlyList<SqlObjectNode>> TriggersAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT name, object_id, is_disabled, is_instead_of_trigger
            FROM {Quote(database)}.sys.triggers
            WHERE parent_id = @id
            ORDER BY name
            """,
            [new SqlParameter("@id", objectId)],
            r => new SqlObjectNode
            {
                Kind = Kinds.Trigger,
                Label = r.GetString(0),
                Detail = (r.GetBoolean(3) ? "Instead of" : "After") + (r.GetBoolean(2) ? ", disabled" : ""),
                Database = database,
                Name = r.GetString(0),
                ObjectId = r.GetInt32(1),
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> PrincipalsAsync(SqlConnection connection, string database, bool roles, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) name, type_desc, default_schema_name
            FROM {Quote(database)}.sys.database_principals
            WHERE {(roles ? "type = 'R'" : "type IN ('S', 'U', 'G', 'E', 'X', 'C', 'K')")}
            ORDER BY name
            """,
            [],
            r => new SqlObjectNode
            {
                Kind = roles ? Kinds.Role : Kinds.User,
                Label = r.GetString(0),
                Detail = roles ? null : PrincipalType(r.GetString(1)),
                Database = database,
                Name = r.GetString(0),
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> SchemasAsync(SqlConnection connection, string database, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) s.name, p.name
            FROM {Quote(database)}.sys.schemas s
            LEFT JOIN {Quote(database)}.sys.database_principals p ON p.principal_id = s.principal_id
            ORDER BY s.name
            """,
            [],
            r => new SqlObjectNode
            {
                Kind = Kinds.SchemaNode,
                Label = r.GetString(0),
                Detail = r.IsDBNull(1) ? null : $"owner {r.GetString(1)}",
                Database = database,
                Name = r.GetString(0),
            },
            cancellationToken);

    private static async Task<IReadOnlyList<SqlObjectNode>> LoginsAsync(SqlConnection connection, bool roles, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"""
            SELECT TOP ({MaxChildren}) name, type_desc, is_disabled
            FROM sys.server_principals
            WHERE {(roles ? "type = 'R'" : "type IN ('S', 'U', 'G', 'E', 'X', 'C', 'K') AND name NOT LIKE '##%'")}
            ORDER BY name
            """,
            [],
            r => new SqlObjectNode
            {
                Kind = roles ? Kinds.ServerRole : Kinds.Login,
                Label = r.GetString(0),
                Detail = roles ? null : PrincipalType(r.GetString(1)) + (r.GetBoolean(2) ? ", disabled" : ""),
                Name = r.GetString(0),
            },
            cancellationToken);

    private static string PrincipalType(string typeDesc) => typeDesc switch
    {
        "SQL_USER" or "SQL_LOGIN" => "SQL",
        "WINDOWS_USER" or "WINDOWS_LOGIN" => "Windows user",
        "WINDOWS_GROUP" => "Windows group",
        "EXTERNAL_USER" or "EXTERNAL_LOGIN" => "Entra ID user",
        "EXTERNAL_GROUPS" or "EXTERNAL_GROUP" => "Entra ID group",
        "CERTIFICATE_MAPPED_USER" or "CERTIFICATE_MAPPED_LOGIN" => "Certificate",
        "ASYMMETRIC_KEY_MAPPED_USER" or "ASYMMETRIC_KEY_MAPPED_LOGIN" => "Asymmetric key",
        _ => typeDesc.ToLowerInvariant(),
    };

    internal static async Task<IReadOnlyList<T>> QueryAsync<T>(
        SqlConnection connection, string sql, SqlParameter[] parameters, Func<SqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }

        return items;
    }
}

/// <summary>A node or script the explorer can't do anything with; the message is for the user.</summary>
internal sealed class SqlExplorerException(string message) : Exception(message);
