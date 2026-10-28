using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>
/// What a plugin package says about itself (<c>manifest.json</c>). The signature covers its exact
/// bytes, and through <see cref="Files"/> the SHA-256 of every other file in the package.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>The package format this manifest is written in.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Lower-case letters, digits and dashes, starting with a letter, e.g. <c>zabbix</c>. Its API lives under <c>/api/plugins/{id}/</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The name shown to people, e.g. "Zabbix".</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version, e.g. <c>1.2.0</c>.</summary>
    public required string Version { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    /// <summary>The plugin SDK version it was built against (<c>major.minor</c>); the server loads it when the major matches and the minor isn't newer.</summary>
    public required string SdkVersion { get; init; }

    /// <summary>The server part: an assembly under <c>backend/</c> with an <c>INKeeperPlugin</c>; null for a frontend-only plugin.</summary>
    public PluginBackend? Backend { get; init; }

    /// <summary>The browser part: a script under <c>frontend/</c> that registers the plugin with the web app; null for a server-only plugin.</summary>
    public PluginFrontend? Frontend { get; init; }

    /// <summary>Plugins this one uses the types of; they're loaded first, and it isn't loaded without them.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Every file in the package besides manifest.json and signature.json, by path, with its SHA-256 (lower-case hex).</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PluginManifest Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions) ?? throw new PluginPackageException("The manifest is empty.");

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>The first problem with the manifest's own fields, or null when they're valid.</summary>
    public string? Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            return $"Package format {FormatVersion} isn't supported (this server reads format {CurrentFormatVersion}).";
        }

        if (!PluginIds.IsValid(Id))
        {
            return $"“{Id}” isn't a valid plugin ID: use 2 to 40 lower-case letters, digits and dashes, starting with a letter.";
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
        {
            return "The plugin needs a name of at most 100 characters.";
        }

        if (!PluginVersions.IsValid(Version))
        {
            return $"“{Version}” isn't a version like 1.2.0.";
        }

        if (!SdkVersions.TryParse(SdkVersion, out _))
        {
            return $"“{SdkVersion}” isn't an SDK version like 1.0.";
        }

        if (Backend is null && Frontend is null)
        {
            return "The plugin has neither a backend nor a frontend.";
        }

        if (Backend is not null && !IsPackagePath(Backend.Assembly, "backend/"))
        {
            return "The backend assembly must be a file under backend/.";
        }

        if (Frontend is not null && (!IsPackagePath(Frontend.Entry, "frontend/") || Frontend.Styles.Any(s => !IsPackagePath(s, "frontend/"))))
        {
            return "The frontend's script and styles must be files under frontend/.";
        }

        if (Dependencies.Any(d => !PluginIds.IsValid(d) || d == Id))
        {
            return "A dependency isn't a valid plugin ID.";
        }

        foreach (var (path, hash) in Files)
        {
            if (!PackagePaths.IsValid(path) || path is PluginPackage.ManifestPath or PluginPackage.SignaturePath)
            {
                return $"“{path}” isn't a valid file path in a package.";
            }

            if (hash.Length != 64 || !hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                return $"The hash of {path} isn't a lower-case SHA-256.";
            }
        }

        var referenced = new[] { Backend?.Assembly, Frontend?.Entry }.Concat(Frontend?.Styles ?? []).OfType<string>();
        return referenced.FirstOrDefault(path => !Files.ContainsKey(path)) is { } missing
            ? $"{missing} is named in the manifest but isn't in the package."
            : null;
    }

    private static bool IsPackagePath(string? path, string under) =>
        path is not null && path.StartsWith(under, StringComparison.Ordinal) && PackagePaths.IsValid(path);
}

public sealed record PluginBackend
{
    /// <summary>The plugin's assembly, e.g. <c>backend/NKeeper.Plugins.Zabbix.dll</c>; its other assemblies sit beside it.</summary>
    public required string Assembly { get; init; }
}

public sealed record PluginFrontend
{
    /// <summary>The script that registers the plugin, e.g. <c>frontend/index.js</c>.</summary>
    public required string Entry { get; init; }

    /// <summary>Stylesheets loaded with it.</summary>
    public IReadOnlyList<string> Styles { get; init; } = [];
}

public static partial class PluginIds
{
    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public static partial class PluginVersions
{
    public static bool IsValid(string? version) => version is not null && version.Length <= 64 && Pattern().IsMatch(version);

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public static class SdkVersions
{
    public static bool TryParse(string? text, out Version version)
    {
        version = null!;
        var parts = text?.Split('.');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new Version(major, minor);
        return true;
    }

    /// <summary>A plugin built against <paramref name="plugin"/> runs on a server with SDK <paramref name="server"/>: same major, and not a newer minor.</summary>
    public static bool IsCompatible(Version plugin, Version server) => plugin.Major == server.Major && plugin.Minor <= server.Minor;
}

/// <summary>File paths inside a package: relative, forward slashes, no empty, <c>.</c> or <c>..</c> segments.</summary>
public static class PackagePaths
{
    public static bool IsValid(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= 260
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." && !segment.Any(char.IsControl));
}
