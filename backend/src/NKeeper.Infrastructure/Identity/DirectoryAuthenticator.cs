using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NKeeper.Infrastructure.Identity;

/// <summary>A directory account whose password checked out.</summary>
/// <param name="UserName">The sAMAccountName, as the directory spells it.</param>
/// <param name="IsAdministrator">Null when no administrators group is configured.</param>
public sealed record DirectoryAccount(string UserName, string? Email, bool? IsAdministrator);

/// <summary>Checks a password against Active Directory.</summary>
public interface IDirectoryAuthenticator
{
    /// <returns>Null for a wrong password, or an account that is disabled, missing or not in the required group.</returns>
    Task<DirectoryAccount?> AuthenticateAsync(string accountName, string password, CancellationToken cancellationToken);
}

/// <summary>Binds to a domain controller as the user (LDAP simple bind over TLS), then reads their account.</summary>
internal sealed class LdapDirectoryAuthenticator(IOptions<ActiveDirectoryOptions> options, ILogger<LdapDirectoryAuthenticator> logger)
    : IDirectoryAuthenticator
{
    private const int InvalidCredentials = 49;

    // LDAP_MATCHING_RULE_IN_CHAIN: membership through nested groups counts.
    private const string InChain = "1.2.840.113556.1.4.1941";

    public Task<DirectoryAccount?> AuthenticateAsync(string accountName, string password, CancellationToken cancellationToken)
    {
        // An empty password is an "unauthenticated bind", which servers accept for any name.
        if (password.Length == 0)
        {
            return Task.FromResult<DirectoryAccount?>(null);
        }

        // System.DirectoryServices.Protocols is synchronous; keep it off request threads.
        return Task.Run(() => Authenticate(options.Value, accountName, password), cancellationToken);
    }

    private DirectoryAccount? Authenticate(ActiveDirectoryOptions settings, string accountName, string password)
    {
        try
        {
            using var connection = new LdapConnection(
                new LdapDirectoryIdentifier(settings.EffectiveServer, settings.EffectivePort),
                new NetworkCredential($"{accountName}@{settings.DnsDomain}", password),
                AuthType.Basic);
            connection.Timeout = settings.Timeout;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            if (settings.Transport == LdapTransport.Ldaps)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else if (settings.Transport == LdapTransport.StartTls)
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }

            try
            {
                connection.Bind();
            }
            catch (LdapException e) when (e.ErrorCode == InvalidCredentials)
            {
                // Also what AD answers for disabled, expired and locked-out accounts.
                logger.LogInformation("Active Directory refused the password for {AccountName}.", accountName);
                return null;
            }

            // Enabled user accounts only (userAccountControl bit 2 is ACCOUNTDISABLE).
            var filter = new StringBuilder("(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))")
                .Append("(sAMAccountName=").Append(Escape(accountName)).Append(')');
            if (!string.IsNullOrWhiteSpace(settings.RequiredGroup))
            {
                filter.Append("(memberOf:").Append(InChain).Append(":=").Append(Escape(settings.RequiredGroup)).Append(')');
            }

            var search = new SearchRequest(
                settings.EffectiveSearchBase, filter.Append(')').ToString(), SearchScope.Subtree, "sAMAccountName", "mail", "userPrincipalName");
            var response = (SearchResponse)connection.SendRequest(search);
            if (response.Entries.Count != 1)
            {
                logger.LogInformation(
                    "Active Directory sign-in for {AccountName} refused: the account is disabled, outside {SearchBase} or not in the required group.",
                    accountName, settings.EffectiveSearchBase);
                return null;
            }

            var entry = response.Entries[0];
            bool? isAdministrator = null;
            if (!string.IsNullOrWhiteSpace(settings.AdministratorsGroup))
            {
                var check = new SearchRequest(
                    entry.DistinguishedName, $"(memberOf:{InChain}:={Escape(settings.AdministratorsGroup)})", SearchScope.Base, "distinguishedName");
                isAdministrator = ((SearchResponse)connection.SendRequest(check)).Entries.Count == 1;
            }

            return new DirectoryAccount(
                Attribute(entry, "sAMAccountName") ?? accountName,
                Attribute(entry, "mail") ?? Attribute(entry, "userPrincipalName"),
                isAdministrator);
        }
        catch (Exception e) when (e is LdapException or DirectoryOperationException)
        {
            logger.LogError(
                e, "Active Directory sign-in failed: couldn't query {Server}:{Port} ({Transport}).",
                settings.EffectiveServer, settings.EffectivePort, settings.Transport);
            return null;
        }
    }

    private static string? Attribute(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) && entry.Attributes[name].GetValues(typeof(string)) is [string value, ..] && value.Length > 0
            ? value
            : null;

    /// <summary>Escapes a value for an LDAP search filter (RFC 4515).</summary>
    internal static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => escaped.Append(@"\5c"),
                '*' => escaped.Append(@"\2a"),
                '(' => escaped.Append(@"\28"),
                ')' => escaped.Append(@"\29"),
                '\0' => escaped.Append(@"\00"),
                _ => escaped.Append(c),
            };
        }

        return escaped.ToString();
    }
}
