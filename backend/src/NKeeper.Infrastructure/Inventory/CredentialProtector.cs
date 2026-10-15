using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace NKeeper.Infrastructure.Inventory;

/// <summary>Encrypts and decrypts credential secrets with ASP.NET Core Data Protection.</summary>
public sealed class CredentialProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("NKeeper.Inventory.Credentials.v1");

    public byte[] Protect(string secret) => _protector.Protect(Encoding.UTF8.GetBytes(secret));

    /// <summary>For the future connect workflow; secrets must never be returned to API clients.</summary>
    public string Unprotect(byte[] protectedSecret) => Encoding.UTF8.GetString(_protector.Unprotect(protectedSecret));
}
