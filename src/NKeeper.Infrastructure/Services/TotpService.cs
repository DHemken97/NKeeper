using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NKeeper.Infrastructure.Services;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 30-second period, 6 digits): the parameters every
/// mainstream authenticator app supports. No third-party dependencies.
/// </summary>
public sealed class TotpService(TimeProvider timeProvider)
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>160-bit secret, as recommended by RFC 4226 §4.</summary>
    public const int SecretSizeBytes = 20;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretSizeBytes));

    public long GetCurrentTimeStep() => timeProvider.GetUtcNow().ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>RFC 4226 HOTP for the given time step.</summary>
    public static string ComputeCode(string base32Secret, long timeStep)
    {
        var key = Base32Decode(base32Secret);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
        CryptographicOperations.ZeroMemory(key);

        // Dynamic truncation (RFC 4226 §5.3).
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            (hash[offset + 1] << 16) |
            (hash[offset + 2] << 8) |
            hash[offset + 3];

        var code = binary % 1_000_000;
        return code.ToString("D6");
    }

    /// <summary>
    /// Checks <paramref name="code"/> against the current time step ± <paramref name="allowedDriftSteps"/>.
    /// Steps at or before <paramref name="lastUsedTimeStep"/> are rejected so a code can't be replayed.
    /// </summary>
    /// <param name="matchedTimeStep">The step that matched; persist it as the new last-used step.</param>
    public bool ValidateCode(
        string base32Secret,
        string? code,
        long? lastUsedTimeStep,
        out long matchedTimeStep,
        int allowedDriftSteps = 1)
    {
        matchedTimeStep = 0;

        var normalized = code?.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (normalized is not { Length: Digits } || !normalized.All(char.IsAsciiDigit))
        {
            return false;
        }

        var candidate = Encoding.ASCII.GetBytes(normalized);
        var currentStep = GetCurrentTimeStep();

        for (var drift = -allowedDriftSteps; drift <= allowedDriftSteps; drift++)
        {
            var step = currentStep + drift;
            if (lastUsedTimeStep is { } last && step <= last)
            {
                continue;
            }

            var expected = Encoding.ASCII.GetBytes(ComputeCode(base32Secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, candidate))
            {
                matchedTimeStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the <c>otpauth://</c> URI that authenticator apps read from a QR code.
    /// See https://github.com/google/google-authenticator/wiki/Key-Uri-Format.
    /// </summary>
    public static string GetQrCodeUri(string issuer, string accountName, string base32Secret)
    {
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}";
        return $"otpauth://totp/{label}" +
               $"?secret={base32Secret.TrimEnd('=')}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>RFC 4648 Base32 without padding.</summary>
    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsInBuffer = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bitsInBuffer - 5)) & 0x1F]);
                bitsInBuffer -= 5;
            }
        }

        if (bitsInBuffer > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>Case-insensitive RFC 4648 Base32 decode; ignores padding, spaces and dashes.</summary>
    public static byte[] Base32Decode(string encoded)
    {
        var output = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bitsInBuffer = 0;

        foreach (var raw in encoded)
        {
            if (raw is '=' or ' ' or '-')
            {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0)
            {
                throw new FormatException($"'{raw}' is not a valid Base32 character.");
            }

            buffer = (buffer << 5) | index;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                output.Add((byte)((buffer >> (bitsInBuffer - 8)) & 0xFF));
                bitsInBuffer -= 8;
            }
        }

        return output.ToArray();
    }
}
