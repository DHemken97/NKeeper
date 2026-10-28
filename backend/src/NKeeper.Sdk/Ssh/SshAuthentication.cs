using System.Net.Sockets;
using System.Text;
using NKeeper.Sdk.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Sdk.Ssh;

/// <summary>SSH authentication and error messages shared by terminal sessions and KVM hosts.</summary>
public static class SshAuthentication
{
    /// <summary>Windows OpenSSH accepts DOMAIN\user for domain accounts.</summary>
    public static string UserName(SessionTarget target) => target.Domain is null ? target.Username : $"{target.Domain}\\{target.Username}";

    public static List<AuthenticationMethod> CreateMethods(string username, SessionTarget target)
    {
        var methods = new List<AuthenticationMethod>();
        if (target.PrivateKey is not null)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(username, LoadPrivateKey(target.PrivateKey, target.Password)));
        }

        if (target.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password));
            // Many servers (e.g. Ubuntu's default) only offer passwords through keyboard-interactive.
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(username);
            keyboardInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(keyboardInteractive);
        }

        return methods;
    }

    /// <summary>Loads a key; the credential's password doubles as the key's passphrase.</summary>
    private static PrivateKeyFile LoadPrivateKey(string privateKey, string? passphrase)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new SessionException(
                SessionErrorCodes.InvalidPrivateKey,
                "The SSH private key couldn't be read. Check its format, and that the credential's password is the key's passphrase if it has one.");
        }
    }

    public static SessionException TranslateConnectError(Exception ex, SessionTarget target, string username) => ex switch
    {
        SshAuthenticationException => new(SessionErrorCodes.AuthenticationFailed, $"Authentication failed for {username}. Check the username, password or key."),
        SocketException socket => new(SessionErrorCodes.ConnectionFailed, $"Couldn't connect to {target.Address}:{target.Port} ({SocketErrors.Describe(socket.SocketErrorCode, "SSH")})."),
        SshOperationTimeoutException or OperationCanceledException or TimeoutException =>
            new(SessionErrorCodes.Timeout, $"Timed out connecting to {target.Address}:{target.Port}."),
        SshConnectionException connection => new(SessionErrorCodes.ConnectionFailed, $"The SSH connection failed: {connection.Message}"),
        _ => new(SessionErrorCodes.ConnectionFailed, $"Couldn't open an SSH session to {target.Address}:{target.Port}."),
    };
}
