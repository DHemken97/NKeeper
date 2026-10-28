using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NKeeper.Plugins.Packaging;

/// <summary>A package that isn't valid, isn't signed by a trusted key, or was changed after it was signed.</summary>
public sealed class PluginPackageException(string message) : Exception(message);

/// <summary><c>signature.json</c>: the Ed25519 signature of manifest.json's exact bytes, and the key that made it.</summary>
public sealed record PluginSignature(string Algorithm, string PublicKey, string Signature)
{
    public const string Ed25519 = "Ed25519";
}

/// <summary>
/// A plugin package (<c>.nkplugin</c>): a zip of <c>manifest.json</c>, <c>signature.json</c>, and the
/// files under <c>backend/</c> and <c>frontend/</c> that the manifest lists with their SHA-256. The
/// signature covers the manifest, so it covers every file; nothing else may be in the zip.
/// </summary>
public static class PluginPackage
{
    public const string Extension = ".nkplugin";
    public const string ManifestPath = "manifest.json";
    public const string SignaturePath = "signature.json";

    /// <summary>Limits that keep a hostile zip from filling the disk.</summary>
    public const int MaxFiles = 5000;

    public const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Writes a signed package from a directory laid out as the package is (<c>backend/</c> and/or
    /// <c>frontend/</c>), listing and hashing every file in it.
    /// </summary>
    public static PluginManifest Write(PluginManifest manifest, string contentDirectory, PluginSigningKey key, string outputPath)
    {
        var root = Path.GetFullPath(contentDirectory);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path is ManifestPath or SignaturePath)
            {
                continue;
            }

            if (!PackagePaths.IsValid(path) || !(path.StartsWith("backend/", StringComparison.Ordinal) || path.StartsWith("frontend/", StringComparison.Ordinal)))
            {
                throw new PluginPackageException($"{path}: a package holds files under backend/ and frontend/ only.");
            }

            using var stream = File.OpenRead(file);
            files[path] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var signed = manifest with { FormatVersion = PluginManifest.CurrentFormatVersion, Files = files };
        if (signed.Validate() is { } problem)
        {
            throw new PluginPackageException(problem);
        }

        var manifestBytes = signed.ToJson();
        var signature = new PluginSignature(PluginSignature.Ed25519, key.PublicKey.ToBase64(), Convert.ToBase64String(key.Sign(manifestBytes)));

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        WriteEntry(zip, ManifestPath, manifestBytes);
        WriteEntry(zip, SignaturePath, JsonSerializer.SerializeToUtf8Bytes(signature, PluginManifest.JsonOptions));
        foreach (var path in files.Keys)
        {
            zip.CreateEntryFromFile(Path.Combine(root, path), path, CompressionLevel.Optimal);
        }

        return signed;
    }

    /// <summary>
    /// Opens a package and checks it completely: a valid manifest, signed by one of
    /// <paramref name="trustedKeys"/>, and every file present, listed and unchanged.
    /// </summary>
    /// <exception cref="PluginPackageException">The package can't be trusted; the message says why.</exception>
    public static VerifiedPluginPackage Verify(string packagePath, IReadOnlyCollection<PluginPublicKey> trustedKeys)
    {
        using var stream = File.OpenRead(packagePath);
        var packageHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new PluginPackageException("The file isn't a plugin package (not a zip).");
        }

        using (zip)
        {
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (!PackagePaths.IsValid(entry.FullName) || !entries.TryAdd(entry.FullName, entry))
                {
                    throw new PluginPackageException($"The package has an invalid or repeated file name: {entry.FullName}");
                }

                total += entry.Length;
                if (entries.Count > MaxFiles || total > MaxTotalBytes)
                {
                    throw new PluginPackageException("The package is too large.");
                }
            }

            var manifestBytes = ReadAll(entries, ManifestPath);
            var signature = ReadSignature(ReadAll(entries, SignaturePath));
            if (signature.Algorithm != PluginSignature.Ed25519 || !PluginPublicKey.TryParse(signature.PublicKey, out var signer))
            {
                throw new PluginPackageException("The package's signature isn't an Ed25519 signature.");
            }

            if (!trustedKeys.Contains(signer))
            {
                throw new PluginPackageException($"The package is signed with key {signer.KeyId}, which this server doesn't trust.");
            }

            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signature.Signature);
            }
            catch (FormatException)
            {
                throw new PluginPackageException("The package's signature isn't valid base64.");
            }

            if (!signer.Verify(manifestBytes, signatureBytes))
            {
                throw new PluginPackageException("The package's signature doesn't match its manifest: it was changed after it was signed.");
            }

            PluginManifest manifest;
            try
            {
                manifest = PluginManifest.Parse(manifestBytes);
            }
            catch (JsonException ex)
            {
                throw new PluginPackageException($"The manifest isn't valid JSON: {ex.Message}");
            }

            if (manifest.Validate() is { } problem)
            {
                throw new PluginPackageException(problem);
            }

            foreach (var path in entries.Keys.Where(p => p is not ManifestPath and not SignaturePath))
            {
                if (!manifest.Files.ContainsKey(path))
                {
                    throw new PluginPackageException($"{path} is in the package but not in its signed manifest.");
                }
            }

            foreach (var (path, hash) in manifest.Files)
            {
                if (!entries.TryGetValue(path, out var entry))
                {
                    throw new PluginPackageException($"{path} is in the manifest but missing from the package.");
                }

                using var content = entry.Open();
                if (Convert.ToHexStringLower(SHA256.HashData(content)) != hash)
                {
                    throw new PluginPackageException($"{path} was changed after the package was signed.");
                }
            }

            return new VerifiedPluginPackage(Path.GetFullPath(packagePath), packageHash, manifest, signer);
        }
    }

    /// <summary>Whether a directory holds exactly the manifest's files, unchanged (an extracted package, before it's loaded again).</summary>
    public static bool MatchesManifest(string directory, PluginManifest manifest)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            return false;
        }

        var present = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(p => p is not ManifestPath)
            .ToHashSet(StringComparer.Ordinal);
        if (!present.SetEquals(manifest.Files.Keys))
        {
            return false;
        }

        foreach (var (path, hash) in manifest.Files)
        {
            using var stream = File.OpenRead(Path.Combine(root, path));
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != hash)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    private static byte[] ReadAll(Dictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(path, out var entry))
        {
            throw new PluginPackageException($"The package has no {path}.");
        }

        if (entry.Length > 4 * 1024 * 1024)
        {
            throw new PluginPackageException($"The package's {path} is too large.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static PluginSignature ReadSignature(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginSignature>(json, PluginManifest.JsonOptions)
                ?? throw new PluginPackageException("The package's signature.json is empty.");
        }
        catch (JsonException)
        {
            throw new PluginPackageException("The package's signature.json isn't valid.");
        }
    }
}

/// <summary>A package that passed <see cref="PluginPackage.Verify"/>.</summary>
public sealed record VerifiedPluginPackage(string Path, string PackageHash, PluginManifest Manifest, PluginPublicKey Signer)
{
    /// <summary>
    /// Extracts the package's listed files into <paramref name="directory"/> (emptied first), with
    /// the manifest beside them, and checks them again after writing.
    /// </summary>
    public void ExtractTo(string directory)
    {
        var root = System.IO.Path.GetFullPath(directory);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        using (var zip = ZipFile.OpenRead(Path))
        {
            foreach (var path in Manifest.Files.Keys)
            {
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
                if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new PluginPackageException($"{path} would be written outside the plugin's directory.");
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                zip.GetEntry(path)!.ExtractToFile(target, overwrite: false);
            }
        }

        File.WriteAllBytes(System.IO.Path.Combine(root, PluginPackage.ManifestPath), Manifest.ToJson());
        if (!PluginPackage.MatchesManifest(root, Manifest))
        {
            throw new PluginPackageException("The extracted files don't match the package.");
        }
    }
}
