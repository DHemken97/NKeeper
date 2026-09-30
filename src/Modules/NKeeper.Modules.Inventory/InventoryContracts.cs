using NKeeper.Core.Entities;

namespace NKeeper.Modules.Inventory;

public sealed record DatacenterDto(Guid Id, string Name, string? Location, string? Description)
{
    public static DatacenterDto From(Datacenter d) => new(d.Id, d.Name, d.Location, d.Description);
}

public sealed record SaveDatacenterRequest(string Name, string? Location, string? Description);

public sealed record ConnectionProfileDto(
    Guid Id,
    Guid NodeId,
    string Name,
    ConnectionProtocol Protocol,
    int Port,
    string? Username,
    string SettingsJson)
{
    public static ConnectionProfileDto From(ConnectionProfile p) =>
        new(p.Id, p.NodeId, p.Name, p.Protocol, p.Port, p.Username, p.SettingsJson);
}

public sealed record NodeDto(
    Guid Id,
    string Name,
    string Hostname,
    NodeOsFamily OsFamily,
    string? Description,
    Guid? DatacenterId,
    IReadOnlyList<ConnectionProfileDto> ConnectionProfiles)
{
    public static NodeDto From(Node n) => new(
        n.Id,
        n.Name,
        n.Hostname,
        n.OsFamily,
        n.Description,
        n.DatacenterId,
        n.ConnectionProfiles.OrderBy(p => p.Name).Select(ConnectionProfileDto.From).ToList());
}

public sealed record SaveNodeRequest(
    string Name,
    string Hostname,
    NodeOsFamily OsFamily,
    string? Description,
    Guid? DatacenterId);

/// <summary>Omit <see cref="Port"/> to use the protocol's default port.</summary>
public sealed record SaveConnectionProfileRequest(
    string Name,
    ConnectionProtocol Protocol,
    int? Port,
    string? Username,
    string? SettingsJson);
