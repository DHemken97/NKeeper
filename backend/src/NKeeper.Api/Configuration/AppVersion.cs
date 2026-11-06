using System.Reflection;

namespace NKeeper.Api.Configuration;

/// <summary>The running build's version, set in Directory.Build.props.</summary>
public static class AppVersion
{
    /// <summary>With the commit when the build could see it, e.g. "1.0.0+9b20fce…".</summary>
    public static string Full { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>For people: "1.0", or "1.0.1" once there's a patch.</summary>
    public static string Display { get; } = Shorten(Full);

    internal static string Shorten(string full)
    {
        var version = full.Split('+')[0];
        return version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 2 ? version[..^2] : version;
    }
}
