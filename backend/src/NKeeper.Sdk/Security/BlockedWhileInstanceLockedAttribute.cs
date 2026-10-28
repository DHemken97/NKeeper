namespace NKeeper.Sdk.Security;

/// <summary>
/// Marks controllers whose changes (anything but GET) are refused with 409 <c>instance_locked</c>
/// while the instance is locked for a move to a new server.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BlockedWhileInstanceLockedAttribute : Attribute;
