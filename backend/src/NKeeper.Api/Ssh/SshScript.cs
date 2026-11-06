using NKeeper.Api.RemoteSessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Api.Ssh;

/// <summary>Running a read-mostly shell script on a Linux host, for the tools that aren't sessions.</summary>
internal static class SshScript
{
    /// <summary>Sets up the tools' usual folders and <c>$p</c>: sudo when it's needed and allowed; nothing otherwise, leaving what the login may do itself.</summary>
    public const string RootIfAllowed =
        "PATH=$PATH:/usr/sbin:/sbin; p=''; if [ \"$(id -u)\" != 0 ]; then if sudo -n true 2>/dev/null; then p='sudo -n'; else echo '@@limited'; fi; fi; ";

    /// <summary>Runs the script; a timeout says <paramref name="what"/> didn't finish.</summary>
    public static async Task<(int Exit, string Output, string Errors)> RunAsync(
        SshClient client, string script, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(script);
        command.CommandTimeout = timeout;
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            throw new SessionException(SessionErrorCodes.Timeout, $"{what} didn't finish in time.");
        }

        return (command.ExitStatus ?? -1, command.Result, command.Error);
    }

    /// <summary>A single shell word, whatever it holds.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The text after the <c>@@limited</c> line, and whether it was there.</summary>
    public static (string Output, bool Limited) WithoutLimitedMarker(string output)
    {
        const string marker = "@@limited\n";
        return output.StartsWith(marker, StringComparison.Ordinal) ? (output[marker.Length..], true) : (output, false);
    }
}
