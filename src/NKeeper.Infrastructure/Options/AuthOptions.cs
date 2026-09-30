namespace NKeeper.Infrastructure.Options;

/// <summary>Bound from <c>Authentication</c>.</summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool LocalDatabaseEnabled { get; set; } = true;

    /// <summary>Create an NKeeper account the first time an AD/PAM user signs in successfully.</summary>
    public bool AutoProvisionExternalUsers { get; set; } = true;

    /// <summary>Failed password or MFA attempts before the account is locked.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Issuer label shown in authenticator apps.</summary>
    public string TotpIssuer { get; set; } = "NKeeper";
}

/// <summary>Bound from <c>Authentication:Jwt</c>.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; set; } = "nkeeper";

    /// <summary>Audience for full session tokens.</summary>
    public string Audience { get; set; } = "nkeeper-api";

    /// <summary>Audience for mfa_pending tokens. Must differ from <see cref="Audience"/> so they can't be used as sessions.</summary>
    public string MfaAudience { get; set; } = "nkeeper-mfa";

    /// <summary>HMAC-SHA256 key, at least 32 characters. Supply via user-secrets or environment variables, never appsettings.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int SessionLifetimeMinutes { get; set; } = 480;

    public int MfaPendingLifetimeMinutes { get; set; } = 5;
}

/// <summary>Bound from <c>Authentication:ActiveDirectory</c>.</summary>
public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "Authentication:ActiveDirectory";

    public bool Enabled { get; set; }

    /// <summary>Domain controller host name or the domain's DNS name.</summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 636;

    /// <summary>Use LDAPS (port 636). Recommended.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>Upgrade a plain LDAP (389) connection with StartTLS. Ignored when <see cref="UseSsl"/> is true.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>Allow sending the password over an unencrypted connection. Lab use only.</summary>
    public bool AllowInsecureBind { get; set; }

    /// <summary>DNS domain (corp.example.com). Used to build UPNs and, if <see cref="BaseDn"/> is empty, the search base.</summary>
    public string Domain { get; set; } = string.Empty;

    public string? BaseDn { get; set; }

    /// <summary>If set, only members of this group (nested membership included) may sign in.</summary>
    public string? RequiredGroupDn { get; set; }

    /// <summary>Members of this group (nested membership included) become NKeeper administrators.</summary>
    public string? AdminGroupDn { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Bound from <c>Authentication:LinuxPam</c>.</summary>
public sealed class LinuxPamOptions
{
    public const string SectionName = "Authentication:LinuxPam";

    public bool Enabled { get; set; }

    /// <summary>PAM service name, i.e. the file under /etc/pam.d/ to use.</summary>
    public string ServiceName { get; set; } = "nkeeper";
}

/// <summary>Bound from <c>Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary><c>Sqlite</c> or <c>Postgres</c>.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Create the schema at startup when it doesn't exist. Replace with migrations once the schema settles.</summary>
    public bool AutoCreate { get; set; } = true;

    /// <summary>
    /// Directory for Data Protection keys (these encrypt TOTP secrets). Must be persistent and
    /// shared between instances in production, or enrolled authenticators stop working.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }
}
