using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NKeeper.Api.Contracts.Inventory;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Inventory;

/// <summary>Manages the shared credential library. Secrets go in encrypted and never come back out.</summary>
public sealed class CredentialManager(NKeeperDbContext db, CredentialProtector protector, TimeProvider timeProvider)
{
    private static readonly Expression<Func<Credential, CredentialResponse>> Projection = c => new CredentialResponse(
        c.Id,
        c.Name,
        c.Username,
        c.Domain,
        c.Notes,
        c.ProtectedPassword != null,
        c.ProtectedPrivateKey != null,
        c.Hosts.Count,
        c.CreatedAtUtc,
        c.UpdatedAtUtc,
        c.Version);

    public Task<List<CredentialResponse>> ListAsync(CancellationToken cancellationToken) =>
        db.Credentials.AsNoTracking().OrderBy(c => c.Name.ToUpper()).ThenBy(c => c.Id).Select(Projection).ToListAsync(cancellationToken);

    public Task<CredentialResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Credentials.AsNoTracking().Where(c => c.Id == id).Select(Projection).FirstOrDefaultAsync(cancellationToken);

    public async Task<CredentialResponse> CreateAsync(CredentialRequest request, Guid userId, CancellationToken cancellationToken)
    {
        var credential = new Credential { CreatedAtUtc = timeProvider.GetUtcNow(), CreatedByUserId = userId };
        Apply(credential, request, userId);
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(credential.Id, cancellationToken))!;
    }

    /// <exception cref="DbUpdateConcurrencyException">The credential changed since <see cref="CredentialRequest.Version"/>.</exception>
    public async Task<CredentialResponse?> UpdateAsync(Guid id, CredentialRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (request.Version is not { } version)
        {
            throw new InventoryValidationException("version", "The version of the credential being edited is required.");
        }

        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        db.Entry(credential).Property(c => c.Version).OriginalValue = version;
        Apply(credential, request, userId);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Deletes the credential and detaches it from every host that used it.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Credentials.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private void Apply(Credential credential, CredentialRequest request, Guid userId)
    {
        credential.Name = request.Name.Trim();
        credential.Username = InventoryText.TrimToNull(request.Username);
        credential.Domain = InventoryText.TrimToNull(request.Domain);
        credential.Notes = InventoryText.TrimToNull(request.Notes);
        credential.UpdatedAtUtc = timeProvider.GetUtcNow();
        credential.UpdatedByUserId = userId;

        // Secrets are stored exactly as entered (no trimming): whitespace can be significant.
        if (!string.IsNullOrEmpty(request.Password))
        {
            credential.ProtectedPassword = protector.Protect(request.Password);
        }
        else if (request.ClearPassword)
        {
            credential.ProtectedPassword = null;
        }

        if (!string.IsNullOrEmpty(request.PrivateKey))
        {
            credential.ProtectedPrivateKey = protector.Protect(request.PrivateKey);
        }
        else if (request.ClearPrivateKey)
        {
            credential.ProtectedPrivateKey = null;
        }
    }
}
