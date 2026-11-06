using System.Diagnostics;
using System.Text.Json;
using NKeeper.Api.RemoteSessions;

namespace NKeeper.Api.Inventory.Scanning;

/// <summary>
/// Runs an Impacket Python script where System.Management isn't available (Linux, Docker). The
/// request, with its login, goes over stdin, never the command line. The script prints one JSON
/// document; on failure, <c>{ error, kind }</c>.
/// </summary>
internal static class ImpacketHelper
{
    /// <param name="scriptPath">The script's path under the API's folder.</param>
    /// <param name="unavailable">What to say when Python can't be started.</param>
    /// <param name="timedOut">What to say when the script doesn't finish in <paramref name="limit"/>.</param>
    public static async Task<JsonElement> RunAsync(string scriptPath, object request, TimeSpan limit, string unavailable, string timedOut, CancellationToken cancellationToken)
    {
        var python = Environment.GetEnvironmentVariable("NKEEPER_WMI_PYTHON") is { Length: > 0 } configured ? configured : "/opt/impacket/bin/python3";
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, scriptPath));

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: timeout.Token);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(await output, process.ExitCode, await errors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new SessionException(SessionErrorCodes.Timeout, timedOut);
        }
    }

    private static JsonElement Parse(string output, int exitCode, string errors)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(output).RootElement;
        }
        catch (JsonException)
        {
            throw new SessionException(SessionErrorCodes.ConnectionFailed, $"The WMI helper failed: {errors.Trim()}");
        }

        if (exitCode != 0 || root.TryGetProperty("error", out _))
        {
            var message = root.TryGetProperty("error", out var error) ? error.GetString() ?? "" : errors;
            var code = root.TryGetProperty("kind", out var kind) && kind.GetString() == "auth" ? SessionErrorCodes.AuthenticationFailed : SessionErrorCodes.ConnectionFailed;
            throw new SessionException(code, message);
        }

        return root;
    }
}
