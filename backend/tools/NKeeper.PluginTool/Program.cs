using NKeeper.Plugins.Packaging;

// nkeeper-plugin: the command-line half of the plugin SDK. See docs/plugins.md.
const string Usage = """
    nkeeper-plugin keygen <key.json>
        Makes a new Ed25519 signing key, and writes its public key beside it (<key>.pub).
    nkeeper-plugin public-key <key.json>
        Prints a signing key's public key, for a server's trusted keys.
    nkeeper-plugin pack --manifest <plugin.json> --key <key.json> --out <file.nkplugin>
                        [--backend <dir>] [--frontend <dir>] [--content <dir>] [--version <x.y.z>]
        Packs and signs a plugin: the manifest (without "files"), the backend's published output,
        the frontend's built files. --content takes a directory already laid out as backend/ and frontend/.
    nkeeper-plugin verify <file.nkplugin> --trust <key.pub | base64 key> [--trust …]
        Checks a package's signature and files, as a server does before loading it.
    """;

try
{
    return args switch
    {
        ["keygen", var path] => Keygen(path),
        ["public-key", var path] => PublicKey(path),
        ["pack", .. var rest] => Pack(Options.Parse(rest)),
        ["verify", var path, .. var rest] => Verify(path, Options.Parse(rest)),
        _ => Fail(Usage),
    };
}
catch (Exception ex) when (ex is PluginPackageException or IOException or FormatException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    return Fail(ex.Message);
}

static int Keygen(string path)
{
    if (File.Exists(path))
    {
        return Fail($"{path} already exists; not overwriting a signing key.");
    }

    var key = PluginSigningKey.Generate();
    File.WriteAllText(path, key.ToJson());
    var publicPath = Path.ChangeExtension(path, ".pub");
    File.WriteAllText(publicPath, $"{key.PublicKey.ToBase64()} nkeeper-plugin-key {key.PublicKey.KeyId}\n");
    Console.WriteLine($"Signing key:  {path} (keep it secret)");
    Console.WriteLine($"Public key:   {publicPath}");
    Console.WriteLine($"Key ID:       {key.PublicKey.KeyId}");
    return 0;
}

static int PublicKey(string path)
{
    var key = PluginSigningKey.FromJson(File.ReadAllText(path));
    Console.WriteLine(key.PublicKey.ToBase64());
    return 0;
}

static int Pack(Options options)
{
    var manifestPath = options.Required("manifest");
    var key = PluginSigningKey.FromJson(File.ReadAllText(options.Required("key")));
    var output = options.Required("out");
    var manifest = PluginManifest.Parse(File.ReadAllBytes(manifestPath));
    if (options.Single("version") is { } version)
    {
        manifest = manifest with { Version = version };
    }

    var staging = Directory.CreateTempSubdirectory("nkeeper-plugin-");
    try
    {
        if (options.Single("content") is { } content)
        {
            CopyDirectory(content, staging.FullName);
        }

        if (options.Single("backend") is { } backend)
        {
            CopyDirectory(backend, Path.Combine(staging.FullName, "backend"));
        }

        if (options.Single("frontend") is { } frontend)
        {
            CopyDirectory(frontend, Path.Combine(staging.FullName, "frontend"));
        }

        if (File.Exists(output))
        {
            File.Delete(output);
        }

        var signed = PluginPackage.Write(manifest, staging.FullName, key, output);
        Console.WriteLine($"{output}: {signed.Id} {signed.Version}, {signed.Files.Count} files, signed with key {key.PublicKey.KeyId}");
        return 0;
    }
    finally
    {
        staging.Delete(recursive: true);
    }
}

static int Verify(string path, Options options)
{
    var keys = new List<PluginPublicKey>();
    foreach (var trust in options.All("trust"))
    {
        var text = File.Exists(trust) ? File.ReadAllText(trust) : trust;
        keys.Add(PluginPublicKey.TryParse(text, out var key) ? key : throw new FormatException($"{trust} isn't a public key."));
    }

    if (keys.Count == 0)
    {
        return Fail("Give at least one --trust key.");
    }

    var package = PluginPackage.Verify(path, keys);
    Console.WriteLine($"OK: {package.Manifest.Id} {package.Manifest.Version}, {package.Manifest.Files.Count} files, signed with key {package.Signer.KeyId}");
    return 0;
}

static void CopyDirectory(string source, string target)
{
    if (!Directory.Exists(source))
    {
        throw new DirectoryNotFoundException($"{source} doesn't exist.");
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var destination = Path.Combine(target, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

/// <summary>--name value pairs; a name may repeat.</summary>
internal sealed class Options
{
    private readonly List<(string Name, string Value)> _values = [];

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unexpected argument: {args[i]}");
            }

            options._values.Add((args[i][2..], args[++i]));
        }

        return options;
    }

    public string? Single(string name) => _values.LastOrDefault(v => v.Name == name).Value;

    public string Required(string name) => Single(name) ?? throw new ArgumentException($"--{name} is required.");

    public IEnumerable<string> All(string name) => _values.Where(v => v.Name == name).Select(v => v.Value);
}
