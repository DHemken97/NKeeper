namespace NKeeper.Infrastructure.Identity;

/// <summary>How the LDAP connection to the domain controller is protected.</summary>
public enum LdapTransport
{
    /// <summary>LDAP over TLS (port 636 by default).</summary>
    Ldaps,

    /// <summary>Plain LDAP upgraded with StartTLS before the bind (port 389 by default).</summary>
    StartTls,

    /// <summary>
    /// Unencrypted: passwords cross the network in clear text. Only for labs; refused unless
    /// <see cref="ActiveDirectoryOptions.AllowInsecureTransport"/> is set.
    /// </summary>
    None,
}

/// <summary>
/// Sign-in with Active Directory accounts (an LDAP simple bind as the user). Users who sign in
/// this way get an NKeeper account automatically, and with
/// <see cref="PassCredentialsToRemoteSessions"/> their domain login can open RDP sessions.
/// </summary>
public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    public bool Enabled { get; set; }

    /// <summary>The domain's NetBIOS name, e.g. CORP: accepted as CORP\user and sent as the RDP domain.</summary>
    public string? Domain { get; set; }

    /// <summary>The domain's DNS name, e.g. corp.example.com: binds use user@DnsDomain.</summary>
    public string? DnsDomain { get; set; }

    /// <summary>A domain controller; the DNS domain (which resolves to the domain's DCs) when unset.</summary>
    public string? Server { get; set; }

    /// <summary>0 means the transport's default: 636 for LDAPS, 389 otherwise.</summary>
    public int Port { get; set; }

    public LdapTransport Transport { get; set; } = LdapTransport.Ldaps;

    public bool AllowInsecureTransport { get; set; }

    /// <summary>Where users are searched for; derived from <see cref="DnsDomain"/> (DC=corp,DC=example,DC=com) when unset.</summary>
    public string? SearchBase { get; set; }

    /// <summary>Distinguished name of a group (nested membership counts) a user must be in to sign in. Anyone in the domain when unset.</summary>
    public string? RequiredGroup { get; set; }

    /// <summary>
    /// Distinguished name of a group whose members are NKeeper administrators, checked at every
    /// sign-in. When unset, administrators assign the role by hand.
    /// </summary>
    public string? AdministratorsGroup { get; set; }

    /// <summary>
    /// Keep the user's domain password, encrypted, with their NKeeper session (until sign-out or
    /// expiry) so RDP sessions can log in as them.
    /// </summary>
    public bool PassCredentialsToRemoteSessions { get; set; } = true;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    internal string EffectiveServer => string.IsNullOrWhiteSpace(Server) ? DnsDomain! : Server;

    internal int EffectivePort => Port != 0 ? Port : Transport == LdapTransport.Ldaps ? 636 : 389;

    internal string EffectiveSearchBase =>
        string.IsNullOrWhiteSpace(SearchBase)
            ? string.Join(',', DnsDomain!.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => $"DC={part}"))
            : SearchBase;

    internal bool IsValid() =>
        !Enabled
        || (!string.IsNullOrWhiteSpace(Domain)
            && !string.IsNullOrWhiteSpace(DnsDomain)
            && Port is >= 0 and <= 65535
            && Timeout > TimeSpan.Zero
            && (Transport != LdapTransport.None || AllowInsecureTransport));

    /// <summary>
    /// The account name in a sign-in identifier: "jdoe", "CORP\jdoe" or "jdoe@corp.example.com".
    /// False for another domain's names, and for addresses that may be a local user's email.
    /// </summary>
    public bool TryGetAccountName(string identifier, out string accountName)
    {
        accountName = string.Empty;
        var name = identifier;
        var backslash = identifier.IndexOf('\\', StringComparison.Ordinal);
        var at = identifier.LastIndexOf('@');
        if (backslash >= 0)
        {
            if (!identifier[..backslash].Equals(Domain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            name = identifier[(backslash + 1)..];
        }
        else if (at >= 0)
        {
            if (!identifier[(at + 1)..].Equals(DnsDomain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            name = identifier[..at];
        }

        // sAMAccountName: at most 20 characters, none of these.
        if (name.Length is 0 or > 20 || name.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@']) >= 0)
        {
            return false;
        }

        accountName = name;
        return true;
    }
}
