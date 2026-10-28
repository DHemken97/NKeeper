using System.Text.Json;
using NKeeper.Api.RemoteSessions;
using NKeeper.Sdk.Sessions;

namespace NKeeper.Api.Idrac;

/// <summary>A server as its iDRAC reports it over Redfish. Every field may be missing on some models or firmware.</summary>
/// <param name="ServiceTag">Dell's service tag (Redfish SKU).</param>
/// <param name="PowerState">"On", "Off", "PoweringOn" or "PoweringOff".</param>
/// <param name="Health">The rolled-up health: "OK", "Warning" or "Critical".</param>
/// <param name="ProcessorCount">Processors (sockets).</param>
/// <param name="LogicalProcessorCount">Threads across all processors, when the BMC reports it (Redfish 2020.4 or later).</param>
/// <param name="PowerActions">The Redfish reset types the server accepts, e.g. "On", "GracefulShutdown", "ForceRestart".</param>
/// <param name="Console">Whether NKeeper can open the HTML5 virtual console, and why not.</param>
public sealed record IdracOverview(
    string? Manufacturer,
    string? Model,
    string? ServiceTag,
    string? SerialNumber,
    string? HostName,
    string? PowerState,
    string? Health,
    string? BiosVersion,
    int? ProcessorCount,
    string? ProcessorModel,
    int? LogicalProcessorCount,
    double? MemoryGiB,
    string? ManagerModel,
    string? FirmwareVersion,
    IReadOnlyList<string> PowerActions,
    ConsoleAvailability Console);

/// <param name="PluginType">The iDRAC's virtual console type when it could be read: "HTML5", "eHTML5", "Java" or "ActiveX".</param>
/// <param name="Problem">Why the console can't be opened from NKeeper; null when it can.</param>
public sealed record ConsoleAvailability(bool Available, string? PluginType, string? Problem);

/// <summary>
/// What NKeeper does with an iDRAC over Redfish: read the server's state, press its power
/// buttons, and get one-time credentials for its HTML5 virtual console. Resources are found from
/// the service root rather than assumed, so status and power also work on other Redfish BMCs.
/// </summary>
internal static class IdracRedfish
{
    private const string KvmSessionAction = "#DelliDRACCardService.GetKVMSession";
    private const string PluginTypeAttribute = "VirtualConsole.1.PluginType";
    private const string ConsoleEnabledAttribute = "VirtualConsole.1.Enable";

    /// <summary>The reset types NKeeper offers, in menu order, when the BMC doesn't list its own.</summary>
    private static readonly string[] DefaultResetTypes = ["On", "GracefulShutdown", "ForceOff", "GracefulRestart", "ForceRestart", "PowerCycle"];

    public static async Task<IdracOverview> GetOverviewAsync(RedfishClient client, CancellationToken cancellationToken)
    {
        var system = await client.GetAsync(await SystemPathAsync(client, cancellationToken), cancellationToken);
        var managerPath = await ManagerPathAsync(client, cancellationToken);
        var manager = await client.GetAsync(managerPath, cancellationToken);

        var processors = Redfish.Number(system, "ProcessorSummary", "Count");
        return new IdracOverview(
            Redfish.String(system, "Manufacturer"),
            Redfish.String(system, "Model"),
            Redfish.String(system, "SKU"),
            Redfish.String(system, "SerialNumber"),
            Redfish.String(system, "HostName"),
            Redfish.String(system, "PowerState"),
            Redfish.String(system, "Status", "HealthRollup") ?? Redfish.String(system, "Status", "Health"),
            Redfish.String(system, "BiosVersion"),
            processors is { } count ? (int)count : null,
            Redfish.String(system, "ProcessorSummary", "Model"),
            Redfish.Number(system, "ProcessorSummary", "LogicalProcessorCount") is { } threads ? (int)threads : null,
            Redfish.Number(system, "MemorySummary", "TotalSystemMemoryGiB"),
            Redfish.String(manager, "Model"),
            Redfish.String(manager, "FirmwareVersion"),
            ResetTypes(system),
            await GetConsoleAvailabilityAsync(client, managerPath, cancellationToken));
    }

    /// <summary>Presses a power button: one of the reset types the server accepts.</summary>
    public static async Task ResetAsync(RedfishClient client, string resetType, CancellationToken cancellationToken)
    {
        var system = await client.GetAsync(await SystemPathAsync(client, cancellationToken), cancellationToken);
        if (!ResetTypes(system).Contains(resetType, StringComparer.Ordinal))
        {
            throw new SessionException(SessionErrorCodes.BadRequest, $"This server doesn't accept the power action “{resetType}”.");
        }

        var target = Redfish.String(system, "Actions", "#ComputerSystem.Reset", "target")
            ?? throw new SessionException(SessionErrorCodes.ConnectionFailed, "This server's BMC doesn't offer power control over Redfish.");
        await client.PostAsync(target, new { ResetType = resetType }, cancellationToken);
    }

    /// <summary>
    /// Asks the iDRAC for one-time console credentials (Dell's GetKVMSession) and returns the path that
    /// opens the HTML5 virtual console with them, as Dell's own launch scripts do.
    /// </summary>
    public static async Task<string> CreateConsoleLoginAsync(RedfishClient client, string username, CancellationToken cancellationToken)
    {
        var managerPath = await ManagerPathAsync(client, cancellationToken);
        var console = await GetConsoleAvailabilityAsync(client, managerPath, cancellationToken);
        if (!console.Available)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, console.Problem!);
        }

        // SessionTypeName is required but not interpreted; Dell's scripts pass the name of a certificate file.
        var session = await client.PostAsync(
            $"{managerPath}/Oem/Dell/DelliDRACCardService/Actions/DelliDRACCardService.GetKVMSession",
            new { SessionTypeName = "ssl_cert.txt" },
            cancellationToken);
        var tempUsername = Redfish.String(session, "TempUsername");
        var tempPassword = Redfish.String(session, "TempPassword");
        if (tempUsername is null || tempPassword is null)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, "The iDRAC didn't hand out console credentials. Check that the virtual console is enabled and licensed.");
        }

        return $"/console?username={Uri.EscapeDataString(username)}&tempUsername={Uri.EscapeDataString(tempUsername)}&tempPassword={Uri.EscapeDataString(tempPassword)}";
    }

    /// <summary>
    /// The console opens through NKeeper when the iDRAC hands out console credentials (iDRAC9 firmware
    /// with GetKVMSession, and a license that includes the virtual console) and the console is HTML5.
    /// </summary>
    private static async Task<ConsoleAvailability> GetConsoleAvailabilityAsync(RedfishClient client, string managerPath, CancellationToken cancellationToken)
    {
        var service = await client.TryGetAsync($"{managerPath}/Oem/Dell/DelliDRACCardService", cancellationToken);
        var actions = service is { } found ? Redfish.Find(found, "Actions") : null;
        if (actions is not { ValueKind: JsonValueKind.Object } list || !list.TryGetProperty(KvmSessionAction, out _))
        {
            return new ConsoleAvailability(
                false,
                null,
                "This BMC can't hand out virtual console sessions over Redfish. NKeeper opens the console of iDRAC9 (firmware 4.x or later); open the web interface to use the console of older iDRACs.");
        }

        var attributes = await client.TryGetAsync($"{managerPath}/Attributes?$select={PluginTypeAttribute},{ConsoleEnabledAttribute}", cancellationToken);
        var pluginType = attributes is { } values ? Redfish.String(values, "Attributes", PluginTypeAttribute) : null;
        var enabled = attributes is { } read ? Redfish.String(read, "Attributes", ConsoleEnabledAttribute) : null;
        if (string.Equals(enabled, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new ConsoleAvailability(false, pluginType, "The virtual console is disabled on this iDRAC. Enable it under iDRAC Settings ▸ Services ▸ Virtual Console.");
        }

        if (pluginType is not null && !pluginType.Contains("HTML5", StringComparison.OrdinalIgnoreCase))
        {
            return new ConsoleAvailability(
                false,
                pluginType,
                $"This iDRAC's virtual console type is {pluginType}, which can't run in a browser tab. Set the plug-in type to HTML5 or eHTML5 under iDRAC Settings ▸ Services ▸ Virtual Console.");
        }

        return new ConsoleAvailability(true, pluginType, null);
    }

    private static string[] ResetTypes(JsonElement system)
    {
        if (Redfish.Find(system, "Actions", "#ComputerSystem.Reset") is not { ValueKind: JsonValueKind.Object } reset)
        {
            return [];
        }

        if (Redfish.Find(reset, "ResetType@Redfish.AllowableValues") is { ValueKind: JsonValueKind.Array } allowed)
        {
            return [.. allowed.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)];
        }

        return DefaultResetTypes;
    }

    private static async Task<string> SystemPathAsync(RedfishClient client, CancellationToken cancellationToken) =>
        Redfish.FirstMember(await client.GetAsync("/redfish/v1/Systems", cancellationToken))
        ?? throw new SessionException(SessionErrorCodes.ConnectionFailed, "The BMC reports no server over Redfish.");

    private static async Task<string> ManagerPathAsync(RedfishClient client, CancellationToken cancellationToken) =>
        Redfish.FirstMember(await client.GetAsync("/redfish/v1/Managers", cancellationToken))
        ?? throw new SessionException(SessionErrorCodes.ConnectionFailed, "The BMC reports no manager over Redfish.");
}
