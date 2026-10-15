using System.ComponentModel.DataAnnotations;

namespace NKeeper.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "NKeeper";

    /// <summary>Populated from <c>ConnectionStrings:NKeeper</c>.</summary>
    [Required(ErrorMessage = "ConnectionStrings:NKeeper must be configured.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Apply pending EF Core migrations when the application starts.</summary>
    public bool MigrateOnStartup { get; set; }
}
