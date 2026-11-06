using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NKeeper.Api.Diagnostics;

/// <summary>
/// Takes what identifies people and servers out of free text before it reaches the diagnostics file.
/// Secrets are removed. Addresses, accounts and names become stable tokens (the same host is always
/// <c>host-1a2b3c4d</c>), keyed by a secret that never leaves the server, so a pattern stays visible
/// without saying which host it is. It's a safety net: the file records as little free text as it can.
/// </summary>
internal sealed partial class DiagnosticsRedactor(byte[] secret)
{
    public const int MaxLength = 4000;
    private const string Removed = "[redacted]";
    private const string Path = "[path]";
    private static readonly TimeSpan NamesTimeout = TimeSpan.FromMilliseconds(500);

    private Regex? _knownNames;

    /// <summary>The host and user names NKeeper knows, which look like any other word to the patterns below.</summary>
    public void SetKnownNames(IEnumerable<string> names)
    {
        var usable = names.Where(name => name.Trim().Length >= 3).Select(name => Regex.Escape(name.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(name => name.Length).Take(5000).ToList();
        _knownNames = usable.Count == 0 ? null : new Regex(
            $@"(?<![\w.-])(?:{string.Join('|', usable)})(?![\w-]|\.\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            NamesTimeout);
    }

    public string Token(string kind, string value)
    {
        var hash = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{kind}\n{value.ToUpperInvariant()}"));
        return $"{kind}-{Convert.ToHexStringLower(hash.AsSpan(0, 4))}";
    }

    /// <param name="keepPaths">For browser stack traces, whose script paths are the point and hold nothing private.</param>
    public string Redact(string text, bool keepPaths = false)
    {
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength] + "…";
        }

        try
        {
            text = PemBlock().Replace(text, Removed);
            text = UrlCredentials().Replace(text, $"$1{Removed}@");
            text = Secrets().Replace(text, $"$1$2{Removed}");
            text = AuthorizationValue().Replace(text, $"$1 {Removed}");
            text = LongBlob().Replace(text, Removed);
            text = Url().Replace(text, match =>
            {
                var host = Token("host", match.Groups[2].Value);
                return keepPaths ? $"{match.Groups[1].Value}://{host}{match.Groups[3].Value}" : $"{match.Groups[1].Value}://{host}";
            });
            text = Account().Replace(text, match => Token("account", match.Value));
            if (!keepPaths)
            {
                text = WindowsPath().Replace(text, Path);
                text = UnixPath().Replace(text, Path);
            }

            text = DomainAccount().Replace(text, match => Token("account", match.Value));
            text = IpV4().Replace(text, match => Token("ip", match.Value));
            text = IpV6OrMac().Replace(text, match => Token("ip", match.Value));
            text = _knownNames?.Replace(text, match => Token("name", match.Value)) ?? text;
            text = LongDnsName().Replace(text, match => Token("host", match.Value));
            return ShortDnsName().Replace(text, match => Token("host", match.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            // Text that couldn't be checked isn't written.
            return "[removed: it couldn't be checked]";
        }
    }

    [GeneratedRegex(@"-----BEGIN [^-]+-----[\s\S]*?(?:-----END [^-]+-----|$)", RegexOptions.None, 500)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\b(password|passwd|pwd|passphrase|secret|token|api[_-]?key|authorization|cookie)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|(?:(?:bearer|basic)\s+)?[^\s;,&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(@"(?<![\w/+-])[A-Za-z0-9+_-]{32,}={0,2}", RegexOptions.None, 500)]
    private static partial Regex LongBlob();

    /// <summary>The scheme, the host, and what follows it without the query string.</summary>
    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*)://([^/\s:?#""'<>]+)([^\s?#""'<>]*)(?:[?#][^\s""'<>]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Url();

    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\w", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Account();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\s""'<>|]*|\\\\[^\s""'<>|]+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w.:/-])~?/(?:[\w.@+~-]+/)+[\w.@+~-]*", RegexOptions.CultureInvariant, 500)]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\b[A-Za-z][\w-]*\\[\w.$-]+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex DomainAccount();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant, 500)]
    private static partial Regex IpV4();

    [GeneratedRegex(@"(?<![\w:])(?=[0-9a-fA-F]*:[0-9a-fA-F]*:)[0-9a-fA-F:]{3,}(?![\w:])", RegexOptions.CultureInvariant, 500)]
    private static partial Regex IpV6OrMac();

    /// <summary>Lower case only: exception text is full of capitalised type names, like System.IO.IOException.</summary>
    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.){2,}[a-z]{2,}\b", RegexOptions.CultureInvariant, 500)]
    private static partial Regex LongDnsName();

    [GeneratedRegex(@"\b[a-z0-9-]+\.(?:com|net|org|local|lan|internal|corp|intranet|home)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex ShortDnsName();
}
