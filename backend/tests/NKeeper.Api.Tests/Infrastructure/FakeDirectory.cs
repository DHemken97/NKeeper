using System.Collections.Concurrent;
using NKeeper.Infrastructure.Identity;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>An Active Directory stand-in: accounts with fixed passwords, as an LDAP bind would check them.</summary>
public sealed class FakeDirectory : IDirectoryAuthenticator
{
    public const string Domain = "CORP";
    public const string DnsDomain = "corp.test";

    private readonly ConcurrentDictionary<string, (string Password, DirectoryAccount Account)> _accounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings that turn Active Directory sign-in on for <see cref="NKeeperApiFactory"/>.</summary>
    public static Dictionary<string, string> Settings(bool passCredentials = true) => new()
    {
        ["ActiveDirectory:Enabled"] = "true",
        ["ActiveDirectory:Domain"] = Domain,
        ["ActiveDirectory:DnsDomain"] = DnsDomain,
        ["ActiveDirectory:PassCredentialsToRemoteSessions"] = passCredentials ? "true" : "false",
    };

    public FakeDirectory Add(string userName, string password, string? email = null, bool? isAdministrator = null)
    {
        _accounts[userName] = (password, new DirectoryAccount(userName, email, isAdministrator));
        return this;
    }

    public Task<DirectoryAccount?> AuthenticateAsync(string accountName, string password, CancellationToken cancellationToken) =>
        Task.FromResult(_accounts.TryGetValue(accountName, out var entry) && entry.Password == password ? entry.Account : null);
}
