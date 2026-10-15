namespace NKeeper.Api.Inventory;

internal static class InventoryText
{
    /// <summary>Trims user input and treats blank values as absent.</summary>
    public static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
