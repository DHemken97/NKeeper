using System.Text.RegularExpressions;

namespace NKeeper.Api.Inventory;

/// <summary>SQL Server instance names, as a SQL Server service stores them.</summary>
internal static partial class SqlServerInstance
{
    /// <summary>The default instance's name: a service stores it as no instance at all.</summary>
    public const string DefaultName = "MSSQLSERVER";

    /// <summary>A trimmed instance name, or null for the default instance (empty or MSSQLSERVER).</summary>
    public static string? Normalize(string? instance)
    {
        var trimmed = InventoryText.TrimToNull(instance);
        return trimmed is null || string.Equals(trimmed, DefaultName, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    /// <summary>What SQL Server Setup accepts: a letter or underscore, then letters, digits, _, $ or #.</summary>
    public static bool IsValid(string instance) => instance.Length <= Infrastructure.Inventory.HostService.InstanceMaxLength && Pattern().IsMatch(instance);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#]*$")]
    private static partial Regex Pattern();
}
