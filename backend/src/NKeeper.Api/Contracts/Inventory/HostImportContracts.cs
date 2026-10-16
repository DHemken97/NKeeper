using System.ComponentModel.DataAnnotations;

namespace NKeeper.Api.Contracts.Inventory;

/// <summary>
/// Imports hosts from an mRemoteNG connection file (confCons.xml or an mRemoteNG XML export).
/// Connections match existing hosts by name (ignoring case).
/// </summary>
public sealed record HostImportRequest : IValidatableObject
{
    public const int MaxContentLength = 20_000_000;

    /// <summary>The file's text.</summary>
    [Required(ErrorMessage = "Choose a file to import.")]
    [StringLength(MaxContentLength, ErrorMessage = "The file is too large (20 MB at most).")]
    public string Content { get; init; } = string.Empty;

    /// <summary>The master password set in mRemoteNG; leave it out when none was set.</summary>
    [StringLength(1024)]
    public string? MasterPassword { get; init; }

    /// <summary>Add connections that match no host.</summary>
    public bool Add { get; init; }

    /// <summary>Update hosts that match a connection with the connection's details.</summary>
    public bool Update { get; init; }

    /// <summary>Delete hosts that match no connection in the file.</summary>
    public bool Delete { get; init; }

    /// <summary>Report what would change without saving anything.</summary>
    public bool DryRun { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Add && !Update && !Delete)
        {
            yield return new ValidationResult("Choose at least one of Add, Update and Delete.", ["add"]);
        }
    }
}

/// <summary>A connection in the file that wasn't imported, and why.</summary>
public sealed record HostImportSkip(string Name, string Reason);

/// <summary>What the import did (or, for a dry run, would do), by host name.</summary>
public sealed record HostImportResponse(
    bool DryRun,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<HostImportSkip> Skipped,
    IReadOnlyList<string> Warnings);
