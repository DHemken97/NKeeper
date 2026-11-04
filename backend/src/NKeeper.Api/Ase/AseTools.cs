using NKeeper.Api.SqlServer;
using Kinds = NKeeper.Api.SqlServer.SqlObjectExplorer.Kinds;

namespace NKeeper.Api.Ase;

/// <summary>ASE's Management tools, grouped as a DBA looks for them; Copy Database on its own at the top.</summary>
internal static class AseTools
{
    private static readonly (string Name, string Label, string Detail, Func<IReadOnlyList<SqlObjectNode>> Tools)[] Folders =
    [
        ("activity", "Activity", "Blocking, resources, errors", () =>
        [
            new SqlObjectNode { Kind = Kinds.Tool, Label = "Blocking Manager", Detail = "Blocking chains", Name = SqlObjectExplorer.BlockingManagerTool },
            Tool(AseHealth.Resources),
            Tool(AseHealth.Errors),
        ]),
        ("storage", "Storage & Backups", "Space, dumps, maintenance", () => [Tool(AseDba.Space), Tool(AseHealth.Dumps), Tool(AseHealth.Maintenance)]),
        ("replication", "Replication", "Health and drift", () => [AseReplication.ToolNode, Tool(AseDba.Drift)]),
        ("security", "Security", "Logins, users, license", () => [Tool(AseHealth.Security), SybaseAdmin.LicenseNode]),
    ];

    public static IReadOnlyList<SqlObjectNode> Management =>
    [
        Tool(AseDba.CopyTool),
        .. Folders.Select(f => new SqlObjectNode { Kind = Kinds.ToolFolder, Label = f.Label, Detail = f.Detail, Name = f.Name, Expandable = true }),
    ];

    public static IReadOnlyList<SqlObjectNode> InFolder(string? name) =>
        Folders.FirstOrDefault(f => f.Name == name).Tools?.Invoke() ?? throw new SqlExplorerException($"There's no “{name}” folder.");

    private static SqlObjectNode Tool(string name) =>
        AseDba.ToolNodes.Concat(AseHealth.ToolNodes).First(n => n.Name == name);
}
