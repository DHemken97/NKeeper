namespace NKeeper.Core.Entities;

public enum MfaCredentialType
{
    Totp,
    Fido2
}

/// <summary>
/// A second factor registered to a user. TOTP uses the secret fields; FIDO2 uses
/// the credential/public-key fields. Unused fields stay null for the other type.
/// </summary>
public class MfaCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public MfaCredentialType Type { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// False until the user proves possession (e.g. enters a valid TOTP code).
    /// Unconfirmed credentials are never used for login.
    /// </summary>
    public bool IsConfirmed { get; set; }

    // --- TOTP ---

    /// <summary>Base32 shared secret, encrypted with ASP.NET Core Data Protection.</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>Last accepted RFC 6238 time step. Codes at or before it are rejected (replay protection).</summary>
    public long? LastUsedTimeStep { get; set; }

    // --- FIDO2 / WebAuthn ---

    public byte[]? CredentialId { get; set; }

    public byte[]? PublicKey { get; set; }

    public uint SignatureCounter { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAtUtc { get; set; }
}
