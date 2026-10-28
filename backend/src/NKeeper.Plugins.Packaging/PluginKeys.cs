using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace NKeeper.Plugins.Packaging;

/// <summary>
/// An Ed25519 public key that plugin packages can be signed with. Servers trust a package when it's
/// signed by one of the keys they were given. Written as one line of base64.
/// </summary>
public sealed class PluginPublicKey : IEquatable<PluginPublicKey>
{
    public const int Length = 32;

    private readonly byte[] _key;

    private PluginPublicKey(byte[] key) => _key = key;

    /// <summary>The first 16 hex digits of the key's SHA-256: short enough to show, long enough to tell keys apart.</summary>
    public string KeyId => Convert.ToHexStringLower(SHA256.HashData(_key))[..16];

    public string ToBase64() => Convert.ToBase64String(_key);

    public override string ToString() => ToBase64();

    public static PluginPublicKey FromBytes(ReadOnlySpan<byte> key) =>
        key.Length == Length ? new PluginPublicKey(key.ToArray()) : throw new FormatException("An Ed25519 public key is 32 bytes.");

    /// <summary>Base64, optionally followed by a space and a comment; blank lines and lines starting with # are skipped.</summary>
    public static bool TryParse(string? text, out PluginPublicKey key)
    {
        key = null!;
        var line = text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        var encoded = line?.Split(' ', 2)[0];
        Span<byte> bytes = stackalloc byte[Length + 1];
        if (encoded is null || !Convert.TryFromBase64String(encoded, bytes, out var written) || written != Length)
        {
            return false;
        }

        key = new PluginPublicKey(bytes[..Length].ToArray());
        return true;
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_key, 0));
        verifier.BlockUpdate(data.ToArray(), 0, data.Length);
        return verifier.VerifySignature(signature.ToArray());
    }

    public bool Equals(PluginPublicKey? other) => other is not null && CryptographicOperations.FixedTimeEquals(_key, other._key);

    public override bool Equals(object? obj) => Equals(obj as PluginPublicKey);

    public override int GetHashCode() => BitConverter.ToInt32(_key, 0);
}

/// <summary>
/// An Ed25519 key pair to sign plugin packages with. Kept as a small JSON file; whoever has it can
/// sign plugins that every server trusting its public key will run, so keep it secret.
/// </summary>
public sealed class PluginSigningKey
{
    private readonly Ed25519PrivateKeyParameters _key;

    private PluginSigningKey(Ed25519PrivateKeyParameters key)
    {
        _key = key;
        PublicKey = PluginPublicKey.FromBytes(key.GeneratePublicKey().GetEncoded());
    }

    public PluginPublicKey PublicKey { get; }

    public static PluginSigningKey Generate() => new(new Ed25519PrivateKeyParameters(new SecureRandom()));

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(data.ToArray(), 0, data.Length);
        return signer.GenerateSignature();
    }

    public string ToJson() => JsonSerializer.Serialize(
        new KeyFile("Ed25519", Convert.ToBase64String(_key.GetEncoded()), PublicKey.ToBase64()),
        PluginManifest.JsonOptions);

    public static PluginSigningKey FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<KeyFile>(json, PluginManifest.JsonOptions);
        if (file is null || file.Algorithm != "Ed25519" || file.PrivateKey is null)
        {
            throw new FormatException("This isn't an NKeeper plugin signing key.");
        }

        var key = new PluginSigningKey(new Ed25519PrivateKeyParameters(Convert.FromBase64String(file.PrivateKey), 0));
        return file.PublicKey is null || key.PublicKey.ToBase64() == file.PublicKey
            ? key
            : throw new FormatException("The signing key's public key doesn't match its private key.");
    }

    private sealed record KeyFile(string Algorithm, string? PrivateKey, string? PublicKey);
}
