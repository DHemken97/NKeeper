using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace NKeeper.Api.SqlServer;

/// <summary>
/// A database user mapped to a login (by SID) that the server no longer has, as after restoring a
/// database onto another server: the user exists, but no one can sign in as it.
/// </summary>
/// <param name="Type">"SQL user", "Windows user" or "Windows group".</param>
/// <param name="Sid">The user's SID, as 0x… hex: a login created with it maps to the user.</param>
/// <param name="MatchingLogin">A login of the same name and kind, which the user can be mapped to (the usual fix after a restore).</param>
/// <param name="OwnedSchemas">Schemas the user owns: it can't be dropped while it owns any.</param>
public sealed record OrphanedUser(string Database, string User, string Type, string Sid, string? DefaultSchema, string? MatchingLogin, IReadOnlyList<string> OwnedSchemas);

/// <summary>A database whose owner (by SID) isn't a login on this server.</summary>
public sealed record OrphanedOwner(string Database, string OwnerSid);

/// <summary>A login users can be mapped to, and which kind of user it suits.</summary>
public sealed record ServerLogin(string Name, bool Windows, bool Disabled);

/// <param name="Skipped">Databases that couldn't be checked, with why (offline, no access, an error).</param>
public sealed record OrphanReport(
    IReadOnlyList<OrphanedUser> Users, IReadOnlyList<OrphanedOwner> Owners, IReadOnlyList<ServerLogin> Logins, IReadOnlyList<string> Skipped, int DatabasesChecked);

/// <summary>
/// Finds and fixes orphaned database users: users whose login is gone or no longer has their SID.
/// Fixes are SQL Server's own statements: <c>ALTER USER … WITH LOGIN</c> (map), <c>CREATE LOGIN … SID</c>
/// (create, keeping the SID), <c>DROP USER</c>, and <c>ALTER AUTHORIZATION ON DATABASE</c> for an owner.
/// </summary>
internal static partial class SqlOrphans
{
    public const string Map = "map";
    public const string Create = "create";
    public const string Drop = "drop";
    public const string Owner = "owner";

    public static async Task<OrphanReport> ReadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var logins = await SqlObjectExplorer.QueryAsync(
            connection,
            "SELECT name, CAST(CASE WHEN type IN ('U', 'G') THEN 1 ELSE 0 END AS bit), is_disabled FROM sys.server_principals WHERE type IN ('S', 'U', 'G') AND name NOT LIKE '##%' ORDER BY name",
            [],
            r => new ServerLogin(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2)),
            cancellationToken);

        var databases = await SqlObjectExplorer.QueryAsync(
            connection,
            """
            SELECT d.name, d.state_desc, CAST(HAS_DBACCESS(d.name) AS bit), CONVERT(varchar(200), d.owner_sid, 1),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.server_principals p WHERE p.sid = d.owner_sid) THEN 1 ELSE 0 END AS bit)
            FROM sys.databases d
            WHERE d.name <> 'tempdb'
            ORDER BY d.name
            """,
            [],
            r => (Name: r.GetString(0), State: r.GetString(1), Access: !r.IsDBNull(2) && r.GetBoolean(2), OwnerSid: r.IsDBNull(3) ? null : r.GetString(3), OwnerExists: r.GetBoolean(4)),
            cancellationToken);

        var users = new List<OrphanedUser>();
        var skipped = new List<string>();
        var checkedCount = 0;
        foreach (var database in databases)
        {
            if (database.State != "ONLINE")
            {
                skipped.Add($"{database.Name}: {database.State.ToLowerInvariant().Replace('_', ' ')}");
                continue;
            }

            if (!database.Access)
            {
                skipped.Add($"{database.Name}: no access");
                continue;
            }

            try
            {
                users.AddRange(await OrphansInAsync(connection, database.Name, logins, cancellationToken));
                checkedCount++;
            }
            catch (SqlException ex)
            {
                skipped.Add($"{database.Name}: {ex.Message}");
            }
        }

        var owners = databases.Where(d => d.OwnerSid is not null && !d.OwnerExists).Select(d => new OrphanedOwner(d.Name, d.OwnerSid!)).ToList();
        return new OrphanReport(users, owners, logins, skipped, checkedCount);
    }

    /// <summary>
    /// Users mapped to an instance login (authentication type INSTANCE: not contained users, users
    /// without a login, or certificate users), other than dbo, guest and the system ones, whose SID no
    /// login has.
    /// </summary>
    private static async Task<IReadOnlyList<OrphanedUser>> OrphansInAsync(
        SqlConnection connection, string database, IReadOnlyList<ServerLogin> logins, CancellationToken cancellationToken)
    {
        var q = SqlObjectExplorer.Quote(database);
        var rows = await SqlObjectExplorer.QueryAsync(
            connection,
            $"""
            SELECT u.name, u.type, CONVERT(varchar(200), u.sid, 1), u.default_schema_name,
                   (SELECT STRING_AGG(CAST(s.name AS nvarchar(max)), N'|') FROM {q}.sys.schemas s WHERE s.principal_id = u.principal_id)
            FROM {q}.sys.database_principals u
            WHERE u.type IN ('S', 'U', 'G')
              AND u.authentication_type = 1
              AND u.principal_id > 4
              AND u.sid IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM sys.server_principals p WHERE p.sid = u.sid)
            ORDER BY u.name
            """,
            [],
            r => (Name: r.GetString(0), Type: r.GetString(1).Trim(), Sid: r.GetString(2), Schema: r.IsDBNull(3) ? null : r.GetString(3), Owned: r.IsDBNull(4) ? "" : r.GetString(4)),
            cancellationToken);

        return [.. rows.Select(row =>
        {
            var windows = row.Type != "S";
            var match = logins.FirstOrDefault(l => l.Windows == windows && string.Equals(l.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            return new OrphanedUser(
                database,
                row.Name,
                row.Type switch { "S" => "SQL user", "U" => "Windows user", _ => "Windows group" },
                row.Sid,
                row.Schema,
                match?.Name,
                row.Owned.Length == 0 ? [] : row.Owned.Split('|'));
        })];
    }

    /// <summary>Runs one fix, and describes it for the audit log (never with the password).</summary>
    /// <exception cref="SqlExplorerException">The fix doesn't apply to that user, or a value is missing.</exception>
    public static async Task<string> FixAsync(SqlConnection connection, SqlOrphanFix fix, CancellationToken cancellationToken) =>
        await FixAsync(connection, fix, await ReadAsync(connection, cancellationToken), cancellationToken);

    /// <summary>
    /// Runs several fixes (map or drop) against one check of the server, each on its own: one that
    /// fails (a user that owns schemas, a permission) doesn't stop the others.
    /// </summary>
    public static async Task<IReadOnlyList<SqlOrphanResult>> FixManyAsync(SqlConnection connection, IReadOnlyList<SqlOrphanFix> fixes, CancellationToken cancellationToken)
    {
        var report = await ReadAsync(connection, cancellationToken);
        var results = new List<SqlOrphanResult>();
        foreach (var fix in fixes)
        {
            try
            {
                results.Add(new SqlOrphanResult(fix.Database, fix.User ?? string.Empty, true, await FixAsync(connection, fix, report, cancellationToken)));
            }
            catch (Exception ex) when (ex is SqlException or SqlExplorerException)
            {
                results.Add(new SqlOrphanResult(fix.Database, fix.User ?? string.Empty, false, ex.Message));
            }
        }

        return results;
    }

    private static async Task<string> FixAsync(SqlConnection connection, SqlOrphanFix fix, OrphanReport report, CancellationToken cancellationToken)
    {
        if (fix.Action == Owner)
        {
            var owner = report.Owners.FirstOrDefault(o => o.Database == fix.Database)
                ?? throw new SqlExplorerException($"The owner of “{fix.Database}” isn't missing (any more). Refresh the list.");
            var login = KnownLogin(report, fix.Login, windows: null);
            await ExecuteAsync(connection, $"ALTER AUTHORIZATION ON DATABASE::{SqlObjectExplorer.Quote(owner.Database)} TO {SqlObjectExplorer.Quote(login.Name)}", null, cancellationToken);
            return $"Owner of {owner.Database} set to {login.Name}";
        }

        // Only users the check found: nothing else can be changed through here.
        var user = report.Users.FirstOrDefault(u => u.Database == fix.Database && u.User == fix.User)
            ?? throw new SqlExplorerException($"“{fix.User}” in “{fix.Database}” isn't orphaned (any more). Refresh the list.");
        var windows = user.Type != "SQL user";
        var inDatabase = SqlObjectExplorer.Quote(user.Database);
        switch (fix.Action)
        {
            case Map:
            {
                var login = KnownLogin(report, fix.Login ?? user.MatchingLogin, windows);
                await ExecuteAsync(connection, $"ALTER USER {SqlObjectExplorer.Quote(user.User)} WITH LOGIN = {SqlObjectExplorer.Quote(login.Name)}", inDatabase, cancellationToken);
                return $"{user.Database}: user {user.User} mapped to login {login.Name}";
            }

            case Create:
            {
                var name = string.IsNullOrWhiteSpace(fix.Login) ? user.User : fix.Login.Trim();
                if (report.Logins.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SqlExplorerException($"A login “{name}” already exists. Map the user to it instead.");
                }

                if (windows)
                {
                    // Windows logins take their SID from the domain, so the user is mapped to it afterwards.
                    await ExecuteAsync(connection, $"CREATE LOGIN {SqlObjectExplorer.Quote(name)} FROM WINDOWS", null, cancellationToken);
                }
                else
                {
                    if (string.IsNullOrEmpty(fix.Password))
                    {
                        throw new SqlExplorerException("Enter a password for the new login.");
                    }

                    // The user's SID: other databases restored with this user map to the login too.
                    if (!SidPattern().IsMatch(user.Sid))
                    {
                        throw new SqlExplorerException("The user's SID can't be used for a login.");
                    }

                    await ExecuteAsync(
                        connection,
                        $"CREATE LOGIN {SqlObjectExplorer.Quote(name)} WITH PASSWORD = {Literal(fix.Password)}, SID = {user.Sid}, DEFAULT_DATABASE = {inDatabase}, CHECK_POLICY = {(fix.CheckPolicy ? "ON" : "OFF")}",
                        null,
                        cancellationToken);
                }

                await ExecuteAsync(connection, $"ALTER USER {SqlObjectExplorer.Quote(user.User)} WITH LOGIN = {SqlObjectExplorer.Quote(name)}", inDatabase, cancellationToken);
                return $"{user.Database}: login {name} created for user {user.User}";
            }

            case Drop:
                if (user.OwnedSchemas.Count > 0)
                {
                    throw new SqlExplorerException(
                        $"“{user.User}” owns {(user.OwnedSchemas.Count == 1 ? "the schema" : "the schemas")} {string.Join(", ", user.OwnedSchemas)}, so it can't be dropped. Give them another owner first, or map the user to a login instead.");
                }

                await ExecuteAsync(connection, $"DROP USER {SqlObjectExplorer.Quote(user.User)}", inDatabase, cancellationToken);
                return $"{user.Database}: user {user.User} dropped";

            default:
                throw new SqlExplorerException("Choose a fix.");
        }
    }

    /// <summary>The fixes for every orphaned user that has a login of its own name: the usual repair after a restore.</summary>
    public static async Task<IReadOnlyList<string>> FixAllMatchedAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var report = await ReadAsync(connection, cancellationToken);
        var done = new List<string>();
        foreach (var user in report.Users.Where(u => u.MatchingLogin is not null))
        {
            await ExecuteAsync(connection, $"ALTER USER {SqlObjectExplorer.Quote(user.User)} WITH LOGIN = {SqlObjectExplorer.Quote(user.MatchingLogin!)}", SqlObjectExplorer.Quote(user.Database), cancellationToken);
            done.Add($"{user.Database}: user {user.User} mapped to login {user.MatchingLogin}");
        }

        return done;
    }

    private static ServerLogin KnownLogin(OrphanReport report, string? name, bool? windows)
    {
        var login = report.Logins.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new SqlExplorerException(string.IsNullOrEmpty(name) ? "Choose a login." : $"There's no login “{name}”.");
        if (windows is { } w && login.Windows != w)
        {
            throw new SqlExplorerException(w ? "A Windows user can only be mapped to a Windows login." : "A SQL user can only be mapped to a SQL login.");
        }

        return login;
    }

    /// <summary>A statement, run in a database when one is given (through its own sp_executesql, as USE would).</summary>
    private static async Task ExecuteAsync(SqlConnection connection, string statement, string? quotedDatabase, CancellationToken cancellationToken)
    {
        await using var command = quotedDatabase is null
            ? new SqlCommand(statement, connection)
            : new SqlCommand($"EXEC {quotedDatabase}.sys.sp_executesql @statement", connection);
        if (quotedDatabase is not null)
        {
            command.Parameters.Add(new SqlParameter("@statement", System.Data.SqlDbType.NVarChar, -1) { Value = statement });
        }

        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>An N'…' literal (CREATE LOGIN takes no variables for its password).</summary>
    private static string Literal(string value) => $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    [GeneratedRegex("^0x[0-9A-Fa-f]{2,170}$")]
    private static partial Regex SidPattern();
}

/// <summary>What one fix of several did: <see cref="Message"/> says what was done, or why it wasn't.</summary>
public sealed record SqlOrphanResult(string Database, string User, bool Succeeded, string Message);

/// <summary>Several fixes at once: mapping users to the logins of their names, or dropping them.</summary>
public sealed record SqlOrphanBulkFix : System.ComponentModel.DataAnnotations.IValidatableObject
{
    public const int MaxFixes = 1000;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(1, ErrorMessage = "Choose some users.")]
    [System.ComponentModel.DataAnnotations.MaxLength(MaxFixes, ErrorMessage = "Fix at most 1000 users at a time.")]
    public SqlOrphanFix[] Fixes { get; init; } = [];

    public IEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> Validate(System.ComponentModel.DataAnnotations.ValidationContext validationContext)
    {
        // Creating logins needs a password each, and owners aren't users: those are one at a time.
        if (Fixes.Any(f => f.Action is not (SqlOrphans.Map or SqlOrphans.Drop) || string.IsNullOrEmpty(f.User)))
        {
            yield return new System.ComponentModel.DataAnnotations.ValidationResult("Only mapping and dropping users can be done in bulk.", ["fixes"]);
        }
    }
}

/// <summary>One fix for an orphaned user (or a database's missing owner).</summary>
public sealed record SqlOrphanFix
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.AllowedValues(SqlOrphans.Map, SqlOrphans.Create, SqlOrphans.Drop, SqlOrphans.Owner, ErrorMessage = "Choose a fix.")]
    public string Action { get; init; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(128)]
    public string Database { get; init; } = string.Empty;

    /// <summary>The user to fix (not for <see cref="SqlOrphans.Owner"/>).</summary>
    [System.ComponentModel.DataAnnotations.StringLength(128)]
    public string? User { get; init; }

    /// <summary>Map: the login (the same-named one when empty). Create: the new login's name (the user's when empty). Owner: the new owner.</summary>
    [System.ComponentModel.DataAnnotations.StringLength(128)]
    public string? Login { get; init; }

    /// <summary>Create, for a SQL user: the new login's password. Never stored or logged.</summary>
    [System.ComponentModel.DataAnnotations.StringLength(128)]
    public string? Password { get; init; }

    /// <summary>Create: enforce Windows password policy on the new SQL login.</summary>
    public bool CheckPolicy { get; init; } = true;
}
