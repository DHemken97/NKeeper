using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NKeeper.Core.Interfaces;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Infrastructure.AuthDrivers;

/// <summary>
/// Authenticates against the host's PAM stack (local accounts, SSSD/Winbind, LDAP, ...).
///
/// Deployment notes:
/// <list type="bullet">
/// <item>Create <c>/etc/pam.d/{ServiceName}</c>, e.g. <c>auth include common-auth</c> / <c>account include common-account</c>.</item>
/// <item>pam_unix can only verify <em>other</em> users' passwords when the process can read /etc/shadow
/// (run as root, or add the service user to the <c>shadow</c> group on Debian/Ubuntu).</item>
/// </list>
/// </summary>
public sealed unsafe partial class LinuxPamAuthDriver(IOptions<LinuxPamOptions> options) : IAuthenticationDriver
{
    private const string LibPam = "libpam.so.0";

    private const int PamSuccess = 0;
    private const int PamBufErr = 5;
    private const int PamConvErr = 19;

    private const int PamPromptEchoOff = 1;
    private const int PamPromptEchoOn = 2;
    private const int PamErrorMsg = 3;
    private const int PamTextInfo = 4;

    private const int PamSilent = 0x8000;
    private const int PamDisallowNullAuthtok = 0x0001;

    // Linux-PAM caps a conversation at 32 messages (PAM_MAX_NUM_MSG).
    private const int PamMaxNumMsg = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct PamMessage
    {
        public int MsgStyle;
        public IntPtr Msg;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamResponse
    {
        public IntPtr Resp;
        public int RespRetcode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamConv
    {
        public IntPtr Conv;
        public IntPtr AppDataPtr;
    }

    private sealed record ConversationState(string Username, string Password);

    [DllImport(LibPam, EntryPoint = "pam_start")]
    private static extern int PamStart(string serviceName, string user, PamConv* conversation, out IntPtr pamHandle);

    [DllImport(LibPam, EntryPoint = "pam_authenticate")]
    private static extern int PamAuthenticate(IntPtr pamHandle, int flags);

    [DllImport(LibPam, EntryPoint = "pam_acct_mgmt")]
    private static extern int PamAcctMgmt(IntPtr pamHandle, int flags);

    [DllImport(LibPam, EntryPoint = "pam_end")]
    private static extern int PamEnd(IntPtr pamHandle, int pamStatus);

    [DllImport(LibPam, EntryPoint = "pam_strerror")]
    private static extern IntPtr PamStrError(IntPtr pamHandle, int errnum);

    // POSIX-ish usernames, plus '@' and '\' for SSSD/Winbind domain users. Rejects whitespace and control characters.
    [GeneratedRegex(@"^[A-Za-z0-9._@][A-Za-z0-9._@\\-]{0,255}$")]
    private static partial Regex UsernamePattern();

    public AuthDriverType DriverType => AuthDriverType.LinuxPam;

    public bool IsEnabled => options.Value.Enabled && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "PAM authentication is only available on Linux hosts."));
        }

        if (!options.Value.Enabled)
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "PAM authentication is disabled."));
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "Username and password are required."));
        }

        username = username.Trim();
        if (!UsernamePattern().IsMatch(username))
        {
            return Task.FromResult(AuthResult.Failure(DriverType, "Username contains unsupported characters."));
        }

        // PAM calls block (and pam_faildelay sleeps on failure); keep them off the request thread.
        return Task.Run(() => Authenticate(options.Value.ServiceName, username, password), ct);
    }

    private AuthResult Authenticate(string serviceName, string username, string password)
    {
        var stateHandle = GCHandle.Alloc(new ConversationState(username, password));
        var pamHandle = IntPtr.Zero;
        var status = PamSuccess;

        try
        {
            var conversation = new PamConv
            {
                Conv = (IntPtr)(delegate* unmanaged<int, PamMessage**, PamResponse**, IntPtr, int>)&Converse,
                AppDataPtr = GCHandle.ToIntPtr(stateHandle)
            };

            status = PamStart(serviceName, username, &conversation, out pamHandle);
            if (status != PamSuccess)
            {
                return AuthResult.Failure(DriverType, $"pam_start failed: {DescribeError(pamHandle, status)}");
            }

            status = PamAuthenticate(pamHandle, PamSilent | PamDisallowNullAuthtok);
            if (status != PamSuccess)
            {
                return AuthResult.Failure(DriverType, $"pam_authenticate failed: {DescribeError(pamHandle, status)}");
            }

            // Account checks: expired, locked, time/host restrictions.
            status = PamAcctMgmt(pamHandle, PamSilent | PamDisallowNullAuthtok);
            if (status != PamSuccess)
            {
                return AuthResult.Failure(DriverType, $"pam_acct_mgmt failed: {DescribeError(pamHandle, status)}");
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, username) };
            return AuthResult.Success(DriverType, username, username, claims);
        }
        catch (DllNotFoundException)
        {
            return AuthResult.Failure(DriverType, $"{LibPam} is not installed on this host.");
        }
        finally
        {
            if (pamHandle != IntPtr.Zero)
            {
                PamEnd(pamHandle, status);
            }

            stateHandle.Free();
        }
    }

    private static string DescribeError(IntPtr pamHandle, int status)
    {
        var message = pamHandle != IntPtr.Zero ? Marshal.PtrToStringUTF8(PamStrError(pamHandle, status)) : null;
        return $"{message ?? "unknown error"} ({status})";
    }

    /// <summary>
    /// PAM conversation callback. Answers password prompts with the password and echo-on prompts
    /// with the username. Responses are allocated with malloc because PAM releases them with free().
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Converse(int numMsg, PamMessage** msg, PamResponse** resp, IntPtr appDataPtr)
    {
        if (numMsg <= 0 || numMsg > PamMaxNumMsg)
        {
            return PamConvErr;
        }

        PamResponse* responses = null;
        try
        {
            if (GCHandle.FromIntPtr(appDataPtr).Target is not ConversationState state)
            {
                return PamConvErr;
            }

            responses = (PamResponse*)NativeMemory.AllocZeroed((nuint)numMsg, (nuint)sizeof(PamResponse));

            for (var i = 0; i < numMsg; i++)
            {
                switch (msg[i]->MsgStyle)
                {
                    case PamPromptEchoOff:
                        responses[i].Resp = AllocUtf8(state.Password);
                        break;
                    case PamPromptEchoOn:
                        responses[i].Resp = AllocUtf8(state.Username);
                        break;
                    case PamErrorMsg:
                    case PamTextInfo:
                        break;
                    default:
                        FreeResponses(responses, numMsg);
                        return PamConvErr;
                }
            }

            *resp = responses;
            return PamSuccess;
        }
        catch
        {
            // Exceptions must never cross an UnmanagedCallersOnly boundary.
            if (responses != null)
            {
                FreeResponses(responses, numMsg);
            }

            return PamBufErr;
        }
    }

    private static IntPtr AllocUtf8(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, byteCount));
        buffer[byteCount] = 0;
        return (IntPtr)buffer;
    }

    private static void FreeResponses(PamResponse* responses, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (responses[i].Resp != IntPtr.Zero)
            {
                var text = (byte*)responses[i].Resp;
                var length = 0;
                while (text[length] != 0)
                {
                    length++;
                }

                // Scrub the password before releasing the buffer.
                NativeMemory.Clear(text, (nuint)length);
                NativeMemory.Free(text);
            }
        }

        NativeMemory.Free(responses);
    }
}
