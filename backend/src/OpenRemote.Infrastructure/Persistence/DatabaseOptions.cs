using System.ComponentModel.DataAnnotations;

namespace OpenRemote.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "OpenRemote";

    /// <summary>Populated from <c>ConnectionStrings:OpenRemote</c>.</summary>
    [Required(ErrorMessage = "ConnectionStrings:OpenRemote must be configured.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Apply pending EF Core migrations when the application starts.</summary>
    public bool MigrateOnStartup { get; set; }
}
