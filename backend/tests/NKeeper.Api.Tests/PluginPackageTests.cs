using System.IO.Compression;
using System.Text;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Api.Tests;

/// <summary>The signed package format: what verifies, and every way a package is refused.</summary>
public sealed class PluginPackageTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("nkeeper-package-test-");
    private readonly PluginSigningKey _key = PluginSigningKey.Generate();

    private static readonly PluginManifest Manifest = new()
    {
        Id = "demo",
        Name = "Demo",
        Version = "1.2.3",
        SdkVersion = "1.0",
        Frontend = new PluginFrontend { Entry = "frontend/index.js", Styles = ["frontend/index.css"] },
    };

    public void Dispose() => _work.Delete(recursive: true);

    [Fact]
    public void Write_ThenVerify_ListsEveryFileWithItsHash()
    {
        var package = WritePackage();

        var verified = PluginPackage.Verify(package, [_key.PublicKey]);

        Assert.Equal("demo", verified.Manifest.Id);
        Assert.Equal(_key.PublicKey.KeyId, verified.Signer.KeyId);
        Assert.Equal(["frontend/index.css", "frontend/index.js", "frontend/nested/chunk.js"], verified.Manifest.Files.Keys.Order());
    }

    [Fact]
    public void Verify_RefusesAKeyTheServerDoesntTrust()
    {
        var package = WritePackage();

        var ex = Assert.Throws<PluginPackageException>(() => PluginPackage.Verify(package, [PluginSigningKey.Generate().PublicKey]));
        Assert.Contains("doesn't trust", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RefusesAFileChangedAfterSigning()
    {
        var package = WritePackage();
        Rewrite(package, "frontend/index.js", "evil()");

        var ex = Assert.Throws<PluginPackageException>(() => PluginPackage.Verify(package, [_key.PublicKey]));
        Assert.Contains("frontend/index.js was changed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RefusesAFileAddedAfterSigning()
    {
        var package = WritePackage();
        Rewrite(package, "backend/extra.dll", "MZ");

        var ex = Assert.Throws<PluginPackageException>(() => PluginPackage.Verify(package, [_key.PublicKey]));
        Assert.Contains("not in its signed manifest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RefusesAManifestChangedAfterSigning()
    {
        var package = WritePackage();
        Rewrite(package, PluginPackage.ManifestPath, Encoding.UTF8.GetString((Manifest with { Version = "9.9.9" }).ToJson()));

        var ex = Assert.Throws<PluginPackageException>(() => PluginPackage.Verify(package, [_key.PublicKey]));
        Assert.Contains("changed after it was signed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RefusesSomethingThatIsntAZip()
    {
        var path = Path.Combine(_work.FullName, "junk.nkplugin");
        File.WriteAllText(path, "not a zip");

        Assert.Throws<PluginPackageException>(() => PluginPackage.Verify(path, [_key.PublicKey]));
    }

    [Fact]
    public void ExtractTo_WritesTheFiles_AndMatchesManifestNoticesChanges()
    {
        var verified = PluginPackage.Verify(WritePackage(), [_key.PublicKey]);
        var target = Path.Combine(_work.FullName, "extracted");

        verified.ExtractTo(target);

        Assert.True(PluginPackage.MatchesManifest(target, verified.Manifest));
        File.AppendAllText(Path.Combine(target, "frontend", "index.js"), "// changed");
        Assert.False(PluginPackage.MatchesManifest(target, verified.Manifest));
    }

    [Theory]
    [InlineData("../evil.js")]
    [InlineData("/etc/passwd")]
    [InlineData("frontend\\index.js")]
    [InlineData("frontend//index.js")]
    [InlineData("C:/evil.js")]
    public void PackagePaths_RefuseEscapesAndOddNames(string path) => Assert.False(PackagePaths.IsValid(path));

    [Theory]
    [InlineData("Zabbix")]
    [InlineData("z")]
    [InlineData("1zabbix")]
    [InlineData("zab_bix")]
    public void PluginIds_MustBeShortLowerCaseNames(string id) => Assert.False(PluginIds.IsValid(id));

    [Fact]
    public void SdkVersions_SameMajorAndNoNewerMinor()
    {
        Assert.True(SdkVersions.IsCompatible(new Version(1, 0), new Version(1, 2)));
        Assert.False(SdkVersions.IsCompatible(new Version(1, 3), new Version(1, 2)));
        Assert.False(SdkVersions.IsCompatible(new Version(2, 0), new Version(1, 9)));
    }

    [Fact]
    public void SigningKey_RoundTripsThroughItsFile()
    {
        var copy = PluginSigningKey.FromJson(_key.ToJson());

        Assert.Equal(_key.PublicKey, copy.PublicKey);
        var signature = copy.Sign("data"u8);
        Assert.True(_key.PublicKey.Verify("data"u8, signature));
        Assert.False(_key.PublicKey.Verify("other"u8, signature));
    }

    private string WritePackage()
    {
        var content = Path.Combine(_work.FullName, "content");
        Directory.CreateDirectory(Path.Combine(content, "frontend", "nested"));
        File.WriteAllText(Path.Combine(content, "frontend", "index.js"), "register()");
        File.WriteAllText(Path.Combine(content, "frontend", "index.css"), ".a{}");
        File.WriteAllText(Path.Combine(content, "frontend", "nested", "chunk.js"), "chunk()");
        var output = Path.Combine(_work.FullName, $"demo-{Guid.NewGuid():N}.nkplugin");
        PluginPackage.Write(Manifest, content, _key, output);
        return output;
    }

    /// <summary>Replaces (or adds) one file in a package, as someone tampering with it would.</summary>
    private static void Rewrite(string package, string path, string content)
    {
        using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
        zip.GetEntry(path)?.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(content);
    }
}
