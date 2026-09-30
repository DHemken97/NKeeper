using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Infrastructure.AuthDrivers;

/// <summary>
/// Validates credentials with an LDAP simple bind against Active Directory as the user,
/// then reads the user's own directory entry for identity and group checks.
/// Works on Windows natively and on Linux with libldap installed.
/// </summary>
public sealed class ActiveDirectoryAuthDriver(
    IOptions<ActiveDirectoryOptions> options,
    ILogger<ActiveDirectoryAuthDriver> logger) : IAuthenticationDriver
{
    private const int LdapInvalidCredentials = 49;

    // LDAP_MATCHING_RULE_IN_CHAIN: matches nested group membership.
    private const string InChainRule = "1.2.840.113556.1.4.1941";

    // userAccountControl ACCOUNTDISABLE flag.
    private const int AccountDisabledFlag = 0x2;

    private static readonly string[] UserAttributes =
        ["objectGUID", "sAMAccountName", "userPrincipalName", "displayName", "mail", "userAccountControl"];

    public AuthDriverType DriverType => AuthDriverType.ActiveDirectory;

    public bool IsEnabled => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Server);

    public Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "Active Directory authentication is disabled."));
        }

        // An empty password turns a simple bind into an anonymous bind, which "succeeds". Never allow it.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "Username and password are required."));
        }

        // LdapConnection is synchronous; keep it off the request thread.
        return Task.Run(() => Authenticate(username.Trim(), password), ct);
    }

    private AuthResult Authenticate(string username, string password)
    {
        var o = options.Value;
        var samAccountName = ExtractSamAccountName(username);
        var bindName = BuildBindName(username, o.Domain);
        var baseDn = string.IsNullOrWhiteSpace(o.BaseDn) ? DomainToBaseDn(o.Domain) : o.BaseDn;

        if (string.IsNullOrEmpty(baseDn))
        {
            return AuthResult.Failure(DriverType, "Active Directory BaseDn or Domain must be configured.");
        }

        var identifier = new LdapDirectoryIdentifier(o.Server, o.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        using var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        try
        {
            if (o.UseSsl)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else if (o.UseStartTls)
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }
            else if (!o.AllowInsecureBind)
            {
                return AuthResult.Failure(DriverType,
                    "Refusing to send credentials over plain LDAP. Enable UseSsl or UseStartTls (or AllowInsecureBind for labs).");
            }

            connection.Bind(new NetworkCredential(bindName, password));
        }
        catch (LdapException ex) when (ex.ErrorCode == LdapInvalidCredentials)
        {
            return AuthResult.Failure(DriverType, "Invalid credentials.");
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or TlsOperationException)
        {
            logger.LogError(ex, "LDAP bind to {Server}:{Port} failed", o.Server, o.Port);
            return AuthResult.Failure(DriverType, "Directory server unavailable.");
        }

        try
        {
            var userFilter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilterValue(samAccountName)}))";
            var entry = FindSingle(connection, baseDn, userFilter);
            if (entry is null)
            {
                return AuthResult.Failure(DriverType, "Bind succeeded but the user entry was not found under the search base.");
            }

            if (int.TryParse(GetString(entry, "userAccountControl"), out var uac) && (uac & AccountDisabledFlag) != 0)
            {
                return AuthResult.Failure(DriverType, "Directory account is disabled.");
            }

            var userDn = entry.DistinguishedName;

            if (!string.IsNullOrWhiteSpace(o.RequiredGroupDn) && !IsMemberOf(connection, userDn, o.RequiredGroupDn))
            {
                return AuthResult.Failure(DriverType, "User is not a member of the required group.");
            }

            var objectGuid = GetBytes(entry, "objectGUID") is { Length: 16 } guidBytes
                ? new Guid(guidBytes).ToString()
                : userDn;

            var canonicalUsername = (GetString(entry, "sAMAccountName") ?? samAccountName).ToLowerInvariant();

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, canonicalUsername),
                new("distinguished_name", userDn)
            };

            if (GetString(entry, "displayName") is { Length: > 0 } displayName)
            {
                claims.Add(new Claim(ClaimTypes.GivenName, displayName));
            }

            if (GetString(entry, "mail") is { Length: > 0 } mail)
            {
                claims.Add(new Claim(ClaimTypes.Email, mail));
            }

            if (GetString(entry, "userPrincipalName") is { Length: > 0 } upn)
            {
                claims.Add(new Claim(ClaimTypes.Upn, upn));
            }

            if (!string.IsNullOrWhiteSpace(o.AdminGroupDn) && IsMemberOf(connection, userDn, o.AdminGroupDn))
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            return AuthResult.Success(DriverType, canonicalUsername, objectGuid, claims);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            logger.LogError(ex, "LDAP search for {Username} failed", samAccountName);
            return AuthResult.Failure(DriverType, "Directory lookup failed.");
        }
    }

    private static SearchResultEntry? FindSingle(LdapConnection connection, string baseDn, string filter)
    {
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, UserAttributes) { SizeLimit = 2 };
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count == 1 ? response.Entries[0] : null;
    }

    /// <summary>Checks nested membership by searching for the user's own DN with an in-chain memberOf filter.</summary>
    private static bool IsMemberOf(LdapConnection connection, string userDn, string groupDn)
    {
        var filter = $"(memberOf:{InChainRule}:={EscapeFilterValue(groupDn)})";
        var request = new SearchRequest(userDn, filter, SearchScope.Base, "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count == 1;
    }

    private static string? GetString(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute))
        {
            return null;
        }

        var values = entry.Attributes[attribute].GetValues(typeof(string));
        return values.Length > 0 ? values[0] as string : null;
    }

    private static byte[]? GetBytes(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute))
        {
            return null;
        }

        var values = entry.Attributes[attribute].GetValues(typeof(byte[]));
        return values.Length > 0 ? values[0] as byte[] : null;
    }

    /// <summary>"CORP\jdoe" → "jdoe", "jdoe@corp.example.com" → "jdoe", "jdoe" → "jdoe".</summary>
    private static string ExtractSamAccountName(string username)
    {
        var backslash = username.IndexOf('\\');
        if (backslash >= 0)
        {
            return username[(backslash + 1)..];
        }

        var at = username.IndexOf('@');
        return at >= 0 ? username[..at] : username;
    }

    /// <summary>Uses DOMAIN\user or a UPN as typed; otherwise appends the configured domain to form a UPN.</summary>
    private static string BuildBindName(string username, string domain)
    {
        if (username.Contains('\\') || username.Contains('@') || string.IsNullOrWhiteSpace(domain))
        {
            return username;
        }

        return $"{username}@{domain}";
    }

    /// <summary>"corp.example.com" → "DC=corp,DC=example,DC=com".</summary>
    private static string DomainToBaseDn(string domain) =>
        string.Join(',', domain
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => $"DC={part}"));

    /// <summary>RFC 4515 escaping so user input can't alter the filter.</summary>
    private static string EscapeFilterValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\5c"); break;
                case '*': builder.Append(@"\2a"); break;
                case '(': builder.Append(@"\28"); break;
                case ')': builder.Append(@"\29"); break;
                case '\0': builder.Append(@"\00"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }
}
