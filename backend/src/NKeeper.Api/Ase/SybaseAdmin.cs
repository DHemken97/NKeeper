using System.Data.Common;
using System.Globalization;
using NKeeper.Api.Databases;
using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>
/// Replication Server's Admin Commands report (its read-only <c>admin</c> commands, each a section), and the
/// License reports of Replication Server (<c>sysadmin lmconfig</c>) and ASE (<c>sp_lmconfig</c>, <c>monLicense</c>).
/// A command a server doesn't support shows its error; the others still run.
/// </summary>
internal static class SybaseAdmin
{
    public const string Admin = "admin";
    public const string License = "license";

    /// <summary>Licenses expiring within this many days are reported.</summary>
    private const int ExpiryWarningDays = 30;

    public static SqlObjectNode AdminNode => new() { Kind = Kinds.Tool, Label = "Admin Commands", Detail = "admin who, disk_space, show_connections…", Name = Admin };

    public static SqlObjectNode LicenseNode => new() { Kind = Kinds.Tool, Label = "License", Detail = "SySAM licenses and expiry", Name = License };

    /// <summary>The read-only admin commands, in the order a DBA reads them: health first, then threads, queues, connections, versions.</summary>
    private static readonly (string Title, string Command)[] AdminCommands =
    [
        ("Version", "admin version"),
        ("RSSD", "admin rssd_name"),
        ("Health", "admin health"),
        ("Down", "admin who_is_down"),
        ("Up", "admin who_is_up"),
        ("Threads", "admin who"),
        ("Stable queues (SQM)", "admin who, sqm"),
        ("Transactions being sorted (SQT)", "admin who, sqt"),
        ("Distributors (DIST)", "admin who, dist"),
        ("Replicate connections (DSI)", "admin who, dsi"),
        ("Routes (RSI)", "admin who, rsi"),
        ("Connections", "admin show_connections"),
        ("Warm standby (logical connections)", "admin logical_status"),
        ("Stable devices", "admin disk_space"),
        ("Backlog", "admin statistics, backlog"),
        ("Route versions", "admin show_route_versions"),
        ("Site version", "admin show_site_version"),
        ("Configuration", "admin config"),
    ];

    public static async Task<ToolReport> AdminAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ToolSection>();
        foreach (var (title, command) in AdminCommands)
        {
            sections.Add(await AseReplication.SectionAsync(connection, title, command, cancellationToken));
        }

        var down = ToolReports.Rows(Section(sections, "admin who_is_down")).Count();
        if (down > 0)
        {
            warnings.Add($"{down} thread{(down == 1 ? " is" : "s are")} down or suspended (admin who_is_down).");
        }

        if (ToolReports.Rows(Section(sections, "admin health")).Any(x => x.Row.Any(c => c?.Contains("SUSPECT", StringComparison.OrdinalIgnoreCase) == true)))
        {
            warnings.Insert(0, "Replication Server reports itself SUSPECT (admin health).");
        }

        foreach (var (columns, row) in ToolReports.Rows(Section(sections, "admin show_connections")))
        {
            if (row.FirstOrDefault(c => c is not null && (c.Contains("Suspend", StringComparison.OrdinalIgnoreCase) || c.Contains("Down", StringComparison.OrdinalIgnoreCase))) is { } state)
            {
                warnings.Add($"Connection {row.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim()} is {state.Trim()} (admin show_connections).");
            }
        }

        foreach (var (columns, row) in ToolReports.Rows(Section(sections, "admin disk_space")))
        {
            var total = ToolReports.Number(columns, row, "Total Segs");
            var used = ToolReports.Number(columns, row, "Used Segs");
            if (total > 0 && used / total >= 0.7)
            {
                warnings.Add($"Stable device {ToolReports.Cell(columns, row, "Partition") ?? ToolReports.Cell(columns, row, "Logical") ?? "?"} is {100 * used / total:0}% full: when it fills, replication stops.");
            }
        }

        return new ToolReport(warnings, [], sections, now);
    }

    /// <summary>Replication Server's licenses: <c>sysadmin lmconfig</c>.</summary>
    public static async Task<ToolReport> ReplicationServerLicenseAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sections = new List<ToolSection>
        {
            await AseReplication.SectionAsync(connection, "License configuration", "sysadmin lmconfig", cancellationToken),
            await AseReplication.SectionAsync(connection, "Version", "admin version", cancellationToken),
        };
        return new ToolReport(LicenseWarnings(sections, now), [], sections, now);
    }

    /// <summary>ASE's licenses: SySAM's configuration, and each license with its expiry (monLicense needs mon_role).</summary>
    public static async Task<ToolReport> AseLicenseAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sections = new List<ToolSection>
        {
            await AseReplication.SectionAsync(
                connection,
                "Licenses",
                "SELECT Name, Quantity, Type, Version, Status, LicenseExpiry, GraceExpiry, LicenseID, Filter, Attributes FROM master..monLicense",
                cancellationToken) with
            {
                Note = "From monLicense: it needs mon_role. Status “graced” means the server runs in its grace period until GraceExpiry.",
            },
            await AseReplication.SectionAsync(connection, "SySAM configuration", "sp_lmconfig", cancellationToken),
            await AseReplication.SectionAsync(connection, "Version", "SELECT @@version", cancellationToken),
        };

        // Older servers lack some monLicense columns: fall back to all of them.
        if (sections[0].Error is not null)
        {
            sections[0] = (await AseReplication.SectionAsync(connection, "Licenses", "SELECT * FROM master..monLicense", cancellationToken)) with { Note = sections[0].Note };
        }

        return new ToolReport(LicenseWarnings(sections, now), [], sections, now);
    }

    /// <summary>Licenses in their grace period, or expiring soon, with the date; any row that mentions a grace period.</summary>
    private static List<string> LicenseWarnings(IEnumerable<ToolSection> sections, DateTimeOffset now)
    {
        var warnings = new List<string>();
        foreach (var section in sections)
        {
            foreach (var (columns, row) in ToolReports.Rows(section))
            {
                var name = ToolReports.Cell(columns, row, "Name") ?? ToolReports.Cell(columns, row, "License Name") ?? row.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "A license";
                var status = ToolReports.Cell(columns, row, "Status") ?? string.Empty;
                var grace = Date(ToolReports.Cell(columns, row, "GraceExpiry"));
                // monLicense says LicenseExpiry; sp_lmconfig says Expiry Date ("Permanent" when it doesn't expire).
                var expiry = Date(ToolReports.Cell(columns, row, "LicenseExpiry") ?? ToolReports.Cell(columns, row, "Expiry Date"));
                if (status.Contains("grace", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Insert(0, $"{name} is running in its grace period{(grace is { } g ? $", which ends {g:yyyy-MM-dd HH:mm}" : "")}: install a license before then, or the feature (or server) stops.");
                }
                else if (expiry is { } e && e - now.UtcDateTime < TimeSpan.FromDays(ExpiryWarningDays))
                {
                    warnings.Add(e < now.UtcDateTime ? $"{name}'s license expired {e:yyyy-MM-dd HH:mm}." : $"{name}'s license expires {e:yyyy-MM-dd HH:mm} ({(e - now.UtcDateTime).TotalDays:0} days).");
                }
                else if (columns.All(c => !c.Name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    && row.FirstOrDefault(c => c?.Contains("grace", StringComparison.OrdinalIgnoreCase) == true) is { } line)
                {
                    warnings.Add($"License information mentions a grace period: {string.Join(" · ", row.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()))}");
                }
            }

            foreach (var message in section.Results.Where(r => r.Kind == "message" && r.Text?.Contains("grace", StringComparison.OrdinalIgnoreCase) == true))
            {
                warnings.Add(message.Text!.Trim());
            }
        }

        return warnings;
    }

    private static ToolSection Section(IEnumerable<ToolSection> sections, string command) => sections.First(s => s.Command == command);

    private static DateTime? Date(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) && date.Year > 1900 && date.Year < 9000 ? date : null;
}
