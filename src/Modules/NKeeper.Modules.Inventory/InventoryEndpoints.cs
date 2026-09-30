using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Entities;
using NKeeper.Core.Security;
using NKeeper.Infrastructure.Data;

namespace NKeeper.Modules.Inventory;

internal static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder group)
    {
        // Reads: any signed-in user. Writes: administrators.
        group.MapGet("/datacenters", ListDatacenters);
        group.MapGet("/nodes", ListNodes);
        group.MapGet("/nodes/{id:guid}", GetNode);

        var admin = group.MapGroup("").RequireAuthorization(NKeeperPolicies.Admin);

        admin.MapPost("/datacenters", CreateDatacenter);
        admin.MapPut("/datacenters/{id:guid}", UpdateDatacenter);
        admin.MapDelete("/datacenters/{id:guid}", DeleteDatacenter);

        admin.MapPost("/nodes", CreateNode);
        admin.MapPut("/nodes/{id:guid}", UpdateNode);
        admin.MapDelete("/nodes/{id:guid}", DeleteNode);

        admin.MapPost("/nodes/{nodeId:guid}/profiles", CreateProfile);
        admin.MapPut("/profiles/{id:guid}", UpdateProfile);
        admin.MapDelete("/profiles/{id:guid}", DeleteProfile);
    }

    // ---------- Datacenters ----------

    private static async Task<Ok<List<DatacenterDto>>> ListDatacenters(AppDbContext db, CancellationToken ct)
    {
        var datacenters = await db.Datacenters.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return TypedResults.Ok(datacenters.Select(DatacenterDto.From).ToList());
    }

    private static async Task<Results<Created<DatacenterDto>, ValidationProblem, Conflict<string>>> CreateDatacenter(
        SaveDatacenterRequest request, AppDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var name = request.Name.Trim();
        if (await db.Datacenters.AnyAsync(d => d.Name == name, ct))
        {
            return TypedResults.Conflict($"A datacenter named '{name}' already exists.");
        }

        var datacenter = new Datacenter { Name = name };
        Apply(datacenter, request);
        db.Datacenters.Add(datacenter);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"datacenters/{datacenter.Id}", DatacenterDto.From(datacenter));
    }

    private static async Task<Results<Ok<DatacenterDto>, NotFound, ValidationProblem, Conflict<string>>> UpdateDatacenter(
        Guid id, SaveDatacenterRequest request, AppDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var datacenter = await db.Datacenters.FindAsync([id], ct);
        if (datacenter is null)
        {
            return TypedResults.NotFound();
        }

        var name = request.Name.Trim();
        if (await db.Datacenters.AnyAsync(d => d.Name == name && d.Id != id, ct))
        {
            return TypedResults.Conflict($"A datacenter named '{name}' already exists.");
        }

        Apply(datacenter, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DatacenterDto.From(datacenter));
    }

    /// <summary>Nodes in the datacenter are kept and become unassigned.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteDatacenter(Guid id, AppDbContext db, CancellationToken ct)
    {
        var datacenter = await db.Datacenters.Include(d => d.Nodes).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (datacenter is null)
        {
            return TypedResults.NotFound();
        }

        db.Datacenters.Remove(datacenter);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- Nodes ----------

    private static async Task<Ok<List<NodeDto>>> ListNodes(AppDbContext db, Guid? datacenterId, CancellationToken ct)
    {
        var query = db.Nodes.AsNoTracking().Include(n => n.ConnectionProfiles).AsQueryable();
        if (datacenterId is not null)
        {
            query = query.Where(n => n.DatacenterId == datacenterId);
        }

        var nodes = await query.OrderBy(n => n.Name).ToListAsync(ct);
        return TypedResults.Ok(nodes.Select(NodeDto.From).ToList());
    }

    private static async Task<Results<Ok<NodeDto>, NotFound>> GetNode(Guid id, AppDbContext db, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().Include(n => n.ConnectionProfiles).SingleOrDefaultAsync(n => n.Id == id, ct);
        return node is null ? TypedResults.NotFound() : TypedResults.Ok(NodeDto.From(node));
    }

    private static async Task<Results<Created<NodeDto>, ValidationProblem>> CreateNode(
        SaveNodeRequest request, AppDbContext db, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return problem;
        }

        var node = new Node { Name = request.Name.Trim(), Hostname = request.Hostname.Trim() };
        Apply(node, request);
        db.Nodes.Add(node);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"nodes/{node.Id}", NodeDto.From(node));
    }

    private static async Task<Results<Ok<NodeDto>, NotFound, ValidationProblem>> UpdateNode(
        Guid id, SaveNodeRequest request, AppDbContext db, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return problem;
        }

        var node = await db.Nodes.Include(n => n.ConnectionProfiles).SingleOrDefaultAsync(n => n.Id == id, ct);
        if (node is null)
        {
            return TypedResults.NotFound();
        }

        Apply(node, request);
        node.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(NodeDto.From(node));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteNode(Guid id, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.Nodes.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    // ---------- Connection profiles ----------

    private static async Task<Results<Created<ConnectionProfileDto>, NotFound, ValidationProblem>> CreateProfile(
        Guid nodeId, SaveConnectionProfileRequest request, AppDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, ct))
        {
            return TypedResults.NotFound();
        }

        var profile = new ConnectionProfile { NodeId = nodeId, Name = request.Name.Trim() };
        Apply(profile, request);
        db.ConnectionProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"profiles/{profile.Id}", ConnectionProfileDto.From(profile));
    }

    private static async Task<Results<Ok<ConnectionProfileDto>, NotFound, ValidationProblem>> UpdateProfile(
        Guid id, SaveConnectionProfileRequest request, AppDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var profile = await db.ConnectionProfiles.FindAsync([id], ct);
        if (profile is null)
        {
            return TypedResults.NotFound();
        }

        Apply(profile, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ConnectionProfileDto.From(profile));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProfile(Guid id, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.ConnectionProfiles.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    // ---------- Mapping ----------

    private static void Apply(Datacenter datacenter, SaveDatacenterRequest request)
    {
        datacenter.Name = request.Name.Trim();
        datacenter.Location = NullIfBlank(request.Location);
        datacenter.Description = NullIfBlank(request.Description);
    }

    private static void Apply(Node node, SaveNodeRequest request)
    {
        node.Name = request.Name.Trim();
        node.Hostname = request.Hostname.Trim();
        node.OsFamily = request.OsFamily;
        node.Description = NullIfBlank(request.Description);
        node.DatacenterId = request.DatacenterId;
    }

    private static void Apply(ConnectionProfile profile, SaveConnectionProfileRequest request)
    {
        profile.Name = request.Name.Trim();
        profile.Protocol = request.Protocol;
        profile.Port = request.Port ?? ConnectionProfile.DefaultPort(request.Protocol);
        profile.Username = NullIfBlank(request.Username);
        profile.SettingsJson = string.IsNullOrWhiteSpace(request.SettingsJson) ? "{}" : request.SettingsJson;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- Validation ----------

    private static ValidationProblem? Validate(SaveDatacenterRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "name", request.Name, 200);
        MaxLength(errors, "location", request.Location, 200);
        MaxLength(errors, "description", request.Description, 2000);
        return ToProblem(errors);
    }

    private static async Task<ValidationProblem?> ValidateAsync(SaveNodeRequest request, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "name", request.Name, 200);
        Require(errors, "hostname", request.Hostname, 255);
        MaxLength(errors, "description", request.Description, 2000);

        if (!Enum.IsDefined(request.OsFamily))
        {
            errors["osFamily"] = ["Unknown OS family."];
        }

        if (request.DatacenterId is { } datacenterId && !await db.Datacenters.AnyAsync(d => d.Id == datacenterId, ct))
        {
            errors["datacenterId"] = ["Datacenter not found."];
        }

        return ToProblem(errors);
    }

    private static ValidationProblem? Validate(SaveConnectionProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "name", request.Name, 200);
        MaxLength(errors, "username", request.Username, 256);

        if (!Enum.IsDefined(request.Protocol))
        {
            errors["protocol"] = ["Unknown protocol."];
        }

        if (request.Port is < 1 or > 65535)
        {
            errors["port"] = ["Port must be between 1 and 65535."];
        }

        if (!string.IsNullOrWhiteSpace(request.SettingsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(request.SettingsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors["settingsJson"] = ["Settings must be a JSON object."];
                }
            }
            catch (JsonException)
            {
                errors["settingsJson"] = ["Settings must be valid JSON."];
            }
        }

        return ToProblem(errors);
    }

    private static void Require(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Required."];
        }
        else
        {
            MaxLength(errors, field, value, maxLength);
        }
    }

    private static void MaxLength(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            errors[field] = [$"Must be {maxLength} characters or fewer."];
        }
    }

    private static ValidationProblem? ToProblem(Dictionary<string, string[]> errors) =>
        errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
}
