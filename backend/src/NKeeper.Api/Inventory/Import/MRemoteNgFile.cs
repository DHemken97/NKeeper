using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NKeeper.Infrastructure.Inventory;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using NKeeper.Sdk.Inventory;

namespace NKeeper.Api.Inventory.Import;

/// <summary>One connection from an mRemoteNG file, with inherited values resolved and its password decrypted.</summary>
/// <param name="FolderPath">The containing folders, outermost first, joined with "/"; null at the top level.</param>
/// <param name="ProtocolLabel">mRemoteNG's protocol name (e.g. "ICA"), kept as the service label when NKeeper has no equivalent.</param>
/// <param name="PasswordProblem">Why the password was left out, when it couldn't be decrypted.</param>
public sealed record MRemoteNgConnection(
    string Name,
    string Hostname,
    string? Description,
    string? MacAddress,
    string? FolderPath,
    ServiceProtocol Protocol,
    string? ProtocolLabel,
    int? Port,
    string? Username,
    string? Domain,
    string? Password,
    string? PasswordProblem);

/// <summary>
/// Reads mRemoteNG's connection file format (confCons.xml, also written by File ▸ Export), version
/// 2.6: nested <c>Node</c> elements (folders and connections) whose attributes hold every setting,
/// with <c>Inherit*</c> flags taking a value from the parent folder.
/// </summary>
/// <remarks>
/// Secrets are AES-GCM encrypted: base64 of salt (16 bytes), nonce (16 bytes), ciphertext and tag,
/// with the key derived by PBKDF2-HMAC-SHA1 (KdfIterations) from the master password, and the salt
/// as associated data. Without a custom master password, mRemoteNG uses "mR3m". The root's
/// Protected attribute is an encrypted marker that shows whether the password is right.
/// </remarks>
public static class MRemoteNgFile
{
    public const string DefaultMasterPassword = "mR3m";

    private const int SaltLength = 16;
    private const int NonceLength = 16;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const double SupportedVersion = 2.6;

    /// <summary>Settings an mRemoteNG node can inherit from its folder (attribute name → its Inherit flag).</summary>
    private static readonly Dictionary<string, string> Inheritable = new(StringComparer.Ordinal)
    {
        ["Protocol"] = "InheritProtocol",
        ["Port"] = "InheritPort",
        ["Username"] = "InheritUsername",
        ["Domain"] = "InheritDomain",
        ["Password"] = "InheritPassword",
        ["Descr"] = "InheritDescription",
        ["MacAddress"] = "InheritMacAddress",
    };

    /// <summary>Parses and decrypts the file.</summary>
    /// <param name="masterPassword">The master password set in mRemoteNG, or null/empty for none.</param>
    /// <exception cref="InventoryValidationException">The file can't be read, or the master password is missing or wrong.</exception>
    public static List<MRemoteNgConnection> Read(string content, string? masterPassword)
    {
        var root = Parse(content).Root;
        if (root is null || root.Name.LocalName != "Connections")
        {
            throw new InventoryValidationException("content", "This isn't an mRemoteNG connection file (confCons.xml or an mRemoteNG XML export).");
        }

        var version = double.TryParse((string?)root.Attribute("ConfVersion"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (version < SupportedVersion)
        {
            throw new InventoryValidationException(
                "content",
                $"This file is from an older mRemoteNG (format {(string?)root.Attribute("ConfVersion") ?? "unknown"}). Open it in a current mRemoteNG, save it, then import it again.");
        }

        var engine = (string?)root.Attribute("EncryptionEngine") ?? "AES";
        var mode = (string?)root.Attribute("BlockCipherMode") ?? "GCM";
        if (!engine.Equals("AES", StringComparison.OrdinalIgnoreCase) || !mode.Equals("GCM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InventoryValidationException(
                "content",
                $"This file uses {engine}-{mode} encryption. NKeeper reads AES-GCM, mRemoteNG's default: choose it in mRemoteNG's Options ▸ Security, save, then import again.");
        }

        var iterations = int.TryParse((string?)root.Attribute("KdfIterations"), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i > 0 ? i : 1000;
        var cipher = new Cipher(ResolvePassword(root, masterPassword, iterations), iterations);

        var nodes = root.Elements().Where(IsNode).ToList();
        if ((bool?)root.Attribute("FullFileEncryption") == true)
        {
            // The whole tree is one encrypted value: the XML of the top-level nodes.
            var tree = cipher.Decrypt(root.Value.Trim())
                ?? throw new InventoryValidationException("content", "The file's encrypted contents couldn't be read.");
            nodes = [.. Parse($"<Nodes>{tree}</Nodes>").Root!.Elements().Where(IsNode)];
        }

        var connections = new List<MRemoteNgConnection>();
        foreach (var node in nodes)
        {
            Collect(node, [], cipher, connections);
        }

        return connections;
    }

    /// <summary>Encrypts a value the way mRemoteNG does (used by tests to build files).</summary>
    public static string Encrypt(string plaintext, string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var gcm = CreateGcm(forEncryption: true, password, salt, nonce, iterations);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[gcm.GetOutputSize(input.Length)];
        var length = gcm.ProcessBytes(input, 0, input.Length, output, 0);
        gcm.DoFinal(output, length);
        return Convert.ToBase64String([.. salt, .. nonce, .. output]);
    }

    private static XDocument Parse(string content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InventoryValidationException("content", $"The file isn't valid XML ({ex.Message}).");
        }
    }

    private static bool IsNode(XElement element) => element.Name.LocalName == "Node";

    /// <summary>Finds the master password that opens the file: the one given, or mRemoteNG's default.</summary>
    private static string ResolvePassword(XElement root, string? masterPassword, int iterations)
    {
        var marker = (string?)root.Attribute("Protected");
        if (string.IsNullOrEmpty(marker))
        {
            return string.IsNullOrEmpty(masterPassword) ? DefaultMasterPassword : masterPassword;
        }

        if (!string.IsNullOrEmpty(masterPassword) && new Cipher(masterPassword, iterations).Decrypt(marker) is not null)
        {
            return masterPassword;
        }

        // A password typed for a file that has none still works.
        if (new Cipher(DefaultMasterPassword, iterations).Decrypt(marker) is not null)
        {
            return DefaultMasterPassword;
        }

        throw new InventoryValidationException(
            "masterPassword",
            string.IsNullOrEmpty(masterPassword)
                ? "This file is protected with a master password. Enter the one set in mRemoteNG."
                : "That master password doesn't open this file.");
    }

    private static void Collect(XElement node, List<XElement> folders, Cipher cipher, List<MRemoteNgConnection> connections)
    {
        var type = (string?)node.Attribute("Type");
        if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
        {
            folders.Add(node);
            foreach (var child in node.Elements().Where(IsNode))
            {
                Collect(child, folders, cipher, connections);
            }

            folders.RemoveAt(folders.Count - 1);
            return;
        }

        if (!string.Equals(type, "Connection", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? Value(string attribute) => Resolve(node, folders, attribute);
        var (protocol, label) = MapProtocol(Value("Protocol"));
        var port = int.TryParse(Value("Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : (int?)null;

        string? password = null, passwordProblem = null;
        if (Value("Password") is { Length: > 0 } encrypted)
        {
            password = cipher.Decrypt(encrypted);
            passwordProblem = password is null ? "its password couldn't be decrypted, so it was left out" : null;
        }

        var folderPath = folders.Count == 0
            ? null
            : string.Join('/', folders.Select(f => ((string?)f.Attribute("Name"))?.Trim()).Where(n => !string.IsNullOrEmpty(n)));

        connections.Add(new MRemoteNgConnection(
            Name: ((string?)node.Attribute("Name"))?.Trim() ?? string.Empty,
            Hostname: ((string?)node.Attribute("Hostname"))?.Trim() ?? string.Empty,
            Description: InventoryText.TrimToNull(Value("Descr")),
            MacAddress: InventoryText.TrimToNull(Value("MacAddress")),
            FolderPath: string.IsNullOrEmpty(folderPath) ? null : folderPath,
            Protocol: protocol,
            ProtocolLabel: label,
            Port: port,
            Username: InventoryText.TrimToNull(Value("Username")),
            Domain: InventoryText.TrimToNull(Value("Domain")),
            Password: string.IsNullOrEmpty(password) ? null : password,
            PasswordProblem: passwordProblem));
    }

    /// <summary>The node's value, or its nearest folder's when the node (and that folder) inherits it.</summary>
    private static string? Resolve(XElement node, List<XElement> folders, string attribute)
    {
        var current = node;
        for (var level = folders.Count - 1; ; level--)
        {
            var inherits = Inheritable.TryGetValue(attribute, out var flag) && (bool?)current.Attribute(flag) == true;
            if (!inherits || level < 0)
            {
                return (string?)current.Attribute(attribute);
            }

            current = folders[level];
        }
    }

    /// <summary>mRemoteNG's protocol names → NKeeper's, with a label for the ones NKeeper only stores.</summary>
    private static (ServiceProtocol Protocol, string? Label) MapProtocol(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "RDP" => (ServiceProtocol.Rdp, null),
        "VNC" => (ServiceProtocol.Vnc, null),
        "SSH1" or "SSH2" => (ServiceProtocol.Ssh, null),
        "TELNET" => (ServiceProtocol.Telnet, null),
        "HTTP" => (ServiceProtocol.Http, null),
        "HTTPS" => (ServiceProtocol.Https, null),
        "POWERSHELL" => (ServiceProtocol.PowerShell, null),
        null or "" => (ServiceProtocol.Other, null),
        _ => (ServiceProtocol.Other, name.Trim()),
    };

    private static GcmBlockCipher CreateGcm(bool forEncryption, string password, byte[] salt, byte[] nonce, int iterations)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA1, KeyLength);
        var gcm = new GcmBlockCipher(new AesEngine());
        gcm.Init(forEncryption, new AeadParameters(new KeyParameter(key), TagLength * 8, nonce, salt));
        return gcm;
    }

    /// <summary>Decrypts mRemoteNG values with one master password.</summary>
    private sealed class Cipher(string password, int iterations)
    {
        /// <returns>The plaintext, or null when the value is malformed or the password is wrong.</returns>
        public string? Decrypt(string value)
        {
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return null;
            }

            if (raw.Length < SaltLength + NonceLength + TagLength)
            {
                return null;
            }

            var gcm = CreateGcm(forEncryption: false, password, raw[..SaltLength], raw[SaltLength..(SaltLength + NonceLength)], iterations);
            var input = raw.AsSpan(SaltLength + NonceLength).ToArray();
            var output = new byte[gcm.GetOutputSize(input.Length)];
            try
            {
                var length = gcm.ProcessBytes(input, 0, input.Length, output, 0);
                length += gcm.DoFinal(output, length);
                return Encoding.UTF8.GetString(output, 0, length);
            }
            catch (InvalidCipherTextException)
            {
                return null;
            }
        }
    }
}
