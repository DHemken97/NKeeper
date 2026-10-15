using System.ComponentModel.DataAnnotations;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>
/// A reusable set of login details. Secret material is stored only in encrypted form
/// (see <see cref="CredentialProtector"/>) and is never exposed through the API.
/// </summary>
public sealed class Credential
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Domain { get; set; }

    /// <summary>Encrypted password (or private-key passphrase). Null when not set.</summary>
    public byte[]? ProtectedPassword { get; set; }

    /// <summary>Encrypted SSH private key. Null when not set.</summary>
    public byte[]? ProtectedPrivateKey { get; set; }

    public string? Notes { get; set; }

    public List<Host> Hosts { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    [Timestamp]
    public uint Version { get; set; }
}
