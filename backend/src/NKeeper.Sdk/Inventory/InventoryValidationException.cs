namespace NKeeper.Sdk.Inventory;

/// <summary>
/// A request that is well-formed but conflicts with stored data (e.g. an unknown group). Thrown
/// from a controller, it answers 400 with a validation problem for <see cref="Field"/>.
/// </summary>
public sealed class InventoryValidationException(string field, string message) : Exception(message)
{
    /// <summary>camelCase JSON property name the error belongs to.</summary>
    public string Field { get; } = field;
}
