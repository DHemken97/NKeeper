using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Api.Tests.Infrastructure;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;

namespace NKeeper.Api.Tests;

/// <summary>Data Protection keys encrypted with a certificate kept outside the database.</summary>
public sealed class KeyRingTests(PostgresServerFixture postgres) : IDisposable
{
    private readonly List<string> _files = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Certificate_EncryptsNewKeys()
    {
        await using var factory = WithCertificate(await postgres.CreateDatabaseAsync(Ct), CreateCertificateFile());
        factory.CreateClient();

        var secret = Protect(factory, "root-secret");
        var keys = await StoredKeysAsync(factory);

        Assert.NotEmpty(keys);
        Assert.All(keys, AssertEncrypted);
        Assert.Equal("root-secret", Unprotect(factory, secret));
    }

    [Fact]
    public async Task Certificate_EncryptsKeysStoredBeforeIt_AndKeepsSecretsReadable()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        byte[] secret;
        await using (var before = new NKeeperApiFactory(connectionString))
        {
            before.CreateClient();
            secret = Protect(before, "root-secret");
            Assert.All(await StoredKeysAsync(before), key => Assert.Contains("<masterKey", key, StringComparison.Ordinal));
        }

        await using var after = WithCertificate(connectionString, CreateCertificateFile());
        after.CreateClient();

        Assert.All(await StoredKeysAsync(after), AssertEncrypted);
        Assert.Equal("root-secret", Unprotect(after, secret));
    }

    [Fact]
    public async Task EncryptedKeys_WithoutTheCertificate_StopStartup()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using (var first = WithCertificate(connectionString, CreateCertificateFile()))
        {
            first.CreateClient();
            Protect(first, "root-secret");
        }

        await using var withoutCertificate = new NKeeperApiFactory(connectionString);
        await using var withAnotherCertificate = WithCertificate(connectionString, CreateCertificateFile());

        Assert.Contains(KeyRingCertificate.PathSetting, StartupError(withoutCertificate), StringComparison.Ordinal);
        Assert.Contains("different certificate", StartupError(withAnotherCertificate), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCertificateFile_StopsStartup()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nkeeper-missing-{Guid.NewGuid():N}.pem");
        await using var factory = WithCertificate(await postgres.CreateDatabaseAsync(Ct), missing);

        Assert.Contains(KeyRingCertificate.PathSetting, StartupError(factory), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private static NKeeperApiFactory WithCertificate(string connectionString, string certificatePath) =>
        new(connectionString, settings: new Dictionary<string, string> { [KeyRingCertificate.PathSetting] = certificatePath });

    private string CreateCertificateFile()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=NKeeper test key ring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"nkeeper-keyring-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, certificate.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        _files.Add(path);
        return path;
    }

    private static void AssertEncrypted(string key)
    {
        Assert.Contains("<encryptedSecret", key, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", key, StringComparison.Ordinal);
    }

    private static string StartupError(NKeeperApiFactory factory)
    {
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }

    private static byte[] Protect(NKeeperApiFactory factory, string secret) =>
        factory.Services.GetRequiredService<CredentialProtector>().Protect(secret);

    private static string Unprotect(NKeeperApiFactory factory, byte[] secret) =>
        factory.Services.GetRequiredService<CredentialProtector>().Unprotect(secret);

    private static async Task<List<string>> StoredKeysAsync(NKeeperApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().DataProtectionKeys.AsNoTracking()
            .Select(k => k.Xml!).ToListAsync(Ct);
    }
}
