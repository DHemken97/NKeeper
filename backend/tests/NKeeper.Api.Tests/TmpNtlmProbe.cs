using System.Net;
using System.Net.Security;

namespace NKeeper.Api.Tests;

public sealed class TmpNtlmProbe
{
    [Fact]
    public void Probe()
    {
        var log = new System.Text.StringBuilder();
        foreach (var (user, pass, domain, target) in new[] {
            ("Dominic", "x", "", "host/MSI"), ("Dominic", "x", "MSI", "host/MSI"), ("Dominic", "", "", "host/MSI"),
            ("Dominic", "x", "", "host/127.0.0.1"), ("a@b.com", "x", "", "host/MSI"), ("Dominic", "x", "", null!) })
        {
            using var auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = "NTLM",
                Credential = new NetworkCredential(user, pass, domain),
                TargetName = target,
                RequiredProtectionLevel = ProtectionLevel.None,
            });
            var blob = auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
            log.Append(user).Append('|').Append(pass.Length).Append('|').Append(domain).Append('|').Append(target).Append(" => ").Append(status).Append(' ').Append(blob?.Length ?? -1).Append('\n');
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ntlmprobe.txt"), log.ToString());
    }
}
