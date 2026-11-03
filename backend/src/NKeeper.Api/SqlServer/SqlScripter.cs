using System.Text;
using Microsoft.Data.SqlClient;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.SqlServer;

/// <summary>Scripts the object explorer offers on a right-click, written as SSMS writes them.</summary>
internal static class SqlScripter
{
    public const string SelectTop = "selectTop";
    public const string Create = "create";
    public const string Execute = "execute";

    /// <summary>Which scripts a node offers.</summary>
    public static bool Offers(string kind, string action) => action switch
    {
        SelectTop => kind is Kinds.Table or Kinds.View or Kinds.Synonym,
        Create => kind is Kinds.Table or Kinds.View or Kinds.Procedure or Kinds.Function or Kinds.Trigger,
        Execute => kind is Kinds.Procedure,
        _ => false,
    };

    public static async Task<string> ScriptAsync(SqlConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken)
    {
        if (!Offers(node.Kind, action) || node.ObjectId is not { } objectId)
        {
            throw new SqlExplorerException("That script isn't offered for this object.");
        }

        var database = SqlObjectExplorer.Required(node.Database);
        return action switch
        {
            SelectTop => await SelectTopAsync(connection, database, node, objectId, cancellationToken),
            Execute => await ExecuteAsync(connection, database, node, objectId, cancellationToken),
            _ => node.Kind == Kinds.Table
                ? await CreateTableAsync(connection, database, node, objectId, cancellationToken)
                : await ModuleAsync(connection, database, objectId, cancellationToken),
        };
    }

    private static string Name(SqlObjectNode node) =>
        $"{SqlObjectExplorer.Quote(node.Schema ?? "dbo")}.{SqlObjectExplorer.Quote(node.Name ?? string.Empty)}";

    private static string Use(string database) => $"USE {SqlObjectExplorer.Quote(database)}\nGO\n\n";

    /// <summary>SSMS's "Select Top 1000 Rows".</summary>
    private static async Task<string> SelectTopAsync(SqlConnection connection, string database, SqlObjectNode node, int objectId, CancellationToken cancellationToken)
    {
        var columns = await SqlObjectExplorer.QueryAsync(
            connection,
            $"SELECT name FROM {SqlObjectExplorer.Quote(database)}.sys.all_columns WHERE object_id = @id ORDER BY column_id",
            [new SqlParameter("@id", objectId)],
            r => SqlObjectExplorer.Quote(r.GetString(0)),
            cancellationToken);
        var list = columns.Count == 0 ? "*" : string.Join("\n      ,", columns);
        return $"/****** Script for SelectTopNRows command from NKeeper ******/\nSELECT TOP (1000) {list}\n  FROM {SqlObjectExplorer.Quote(database)}.{Name(node)}";
    }

    /// <summary>A view's, procedure's, function's or trigger's own definition, as CREATE.</summary>
    private static async Task<string> ModuleAsync(SqlConnection connection, string database, int objectId, CancellationToken cancellationToken)
    {
        var definitions = await SqlObjectExplorer.QueryAsync(
            connection,
            $"SELECT definition FROM {SqlObjectExplorer.Quote(database)}.sys.all_sql_modules WHERE object_id = @id",
            [new SqlParameter("@id", objectId)],
            r => r.IsDBNull(0) ? null : r.GetString(0),
            cancellationToken);
        var definition = definitions.Count == 0
            ? throw new SqlExplorerException("This object has no definition to script (it may be a CLR object).")
            : definitions[0] ?? throw new SqlExplorerException("This object's definition is encrypted, or you don't have permission to see it (VIEW DEFINITION).");
        return $"{Use(database)}{definition.Trim()}\nGO\n";
    }

    /// <summary>A procedure call with its parameters, for "Execute Stored Procedure".</summary>
    private static async Task<string> ExecuteAsync(SqlConnection connection, string database, SqlObjectNode node, int objectId, CancellationToken cancellationToken)
    {
        var parameters = await SqlObjectExplorer.QueryAsync(
            connection,
            $"""
            SELECT c.name, {SqlObjectExplorer.TypeExpression(database)}, c.is_output
            FROM {SqlObjectExplorer.Quote(database)}.sys.all_parameters c
            JOIN {SqlObjectExplorer.Quote(database)}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = @id AND c.parameter_id > 0
            ORDER BY c.parameter_id
            """,
            [new SqlParameter("@id", objectId)],
            r => (Name: r.GetString(0), Type: r.GetString(1), Output: r.GetBoolean(2)),
            cancellationToken);
        var script = new StringBuilder(Use(database)).Append("DECLARE @RC int\n");
        foreach (var p in parameters)
        {
            script.Append("DECLARE ").Append(p.Name).Append(' ').Append(p.Type).Append('\n');
        }

        script.Append("\n-- TODO: Set parameter values here.\n\nEXECUTE @RC = ").Append(Name(node));
        script.Append(string.Join(",", parameters.Select((p, i) => $"{(i == 0 ? " " : "\n   ")}{p.Name}{(p.Output ? " OUTPUT" : "")}")));
        return script.Append("\nGO\n").ToString();
    }

    /// <summary>A table's columns (types, nullability, identity, defaults, computed) and its primary key.</summary>
    private static async Task<string> CreateTableAsync(SqlConnection connection, string database, SqlObjectNode node, int objectId, CancellationToken cancellationToken)
    {
        var q = SqlObjectExplorer.Quote(database);
        var columns = await SqlObjectExplorer.QueryAsync(
            connection,
            $"""
            SELECT c.name, {SqlObjectExplorer.TypeExpression(database)}, c.is_nullable, c.is_identity,
                   CAST(ic.seed_value AS nvarchar(40)), CAST(ic.increment_value AS nvarchar(40)),
                   cc.definition, d.name, d.definition
            FROM {q}.sys.columns c
            JOIN {q}.sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN {q}.sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN {q}.sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN {q}.sys.default_constraints d ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """,
            [new SqlParameter("@id", objectId)],
            r =>
            {
                var line = new StringBuilder("\t").Append(SqlObjectExplorer.Quote(r.GetString(0)));
                if (!r.IsDBNull(6))
                {
                    return line.Append(" AS ").Append(r.GetString(6)).ToString();
                }

                line.Append(' ').Append(r.GetString(1));
                if (r.GetBoolean(3))
                {
                    line.Append(" IDENTITY(").Append(r.IsDBNull(4) ? "1" : r.GetString(4)).Append(',').Append(r.IsDBNull(5) ? "1" : r.GetString(5)).Append(')');
                }

                line.Append(r.GetBoolean(2) ? " NULL" : " NOT NULL");
                if (!r.IsDBNull(7))
                {
                    line.Append(" CONSTRAINT ").Append(SqlObjectExplorer.Quote(r.GetString(7))).Append(" DEFAULT ").Append(r.GetString(8));
                }

                return line.ToString();
            },
            cancellationToken);

        var keys = await SqlObjectExplorer.QueryAsync(
            connection,
            $"""
            SELECT k.name, i.type_desc, c.name, ic.is_descending_key, k.type
            FROM {q}.sys.key_constraints k
            JOIN {q}.sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
            JOIN {q}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN {q}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE k.parent_object_id = @id
            ORDER BY k.type, k.name, ic.key_ordinal
            """,
            [new SqlParameter("@id", objectId)],
            r => (Name: r.GetString(0), Type: r.GetString(1), Column: r.GetString(2), Descending: r.GetBoolean(3), Primary: r.GetString(4).Trim() == "PK"),
            cancellationToken);

        var lines = columns.ToList();
        lines.AddRange(keys.GroupBy(k => k.Name).Select(key =>
        {
            var first = key.First();
            var kind = first.Primary ? "PRIMARY KEY" : "UNIQUE";
            var cols = string.Join(", ", key.Select(k => SqlObjectExplorer.Quote(k.Column) + (k.Descending ? " DESC" : " ASC")));
            return $" CONSTRAINT {SqlObjectExplorer.Quote(key.Key)} {kind} {(first.Type == "CLUSTERED" ? "CLUSTERED" : "NONCLUSTERED")}\n(\n\t{cols}\n)";
        }));

        return $"{Use(database)}CREATE TABLE {Name(node)}(\n{string.Join(",\n", lines)}\n)\nGO\n";
    }
}
