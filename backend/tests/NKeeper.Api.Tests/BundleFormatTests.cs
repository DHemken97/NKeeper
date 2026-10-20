using System.Buffers.Binary;
using System.Security.Cryptography;
using NKeeper.Api.Transfer;

namespace NKeeper.Api.Tests;

/// <summary>The bundle's chunked encryption: chunks can't be reordered, dropped or added.</summary>
public sealed class BundleFormatTests
{
    private const string Passphrase = "a passphrase long enough";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManyChunks_RoundTrip_AndAnyRearrangementIsRefused()
    {
        var plaintext = RandomNumberGenerator.GetBytes((64 * 1024 * 3) + 1000);
        var bundle = await WriteAsync(plaintext);

        var (header, read) = BundleFormat.Read(bundle, Passphrase);
        Assert.Equal(plaintext, read);
        Assert.Equal("tester", header.ExportedBy);

        var chunks = Chunks(bundle, out var prefixLength);
        Assert.Equal(4, chunks.Count);
        byte[] Assemble(IEnumerable<byte[]> parts) => [.. bundle[..prefixLength], .. parts.SelectMany(p => p)];

        Assert.Throws<BundleException>(() => BundleFormat.Read(Assemble([chunks[0], chunks[2], chunks[1], chunks[3]]), Passphrase));
        // Cut at a chunk boundary: the new last chunk wasn't written as the last one.
        Assert.Throws<BundleException>(() => BundleFormat.Read(Assemble(chunks.Take(3)), Passphrase));
        // A chunk added at the end (the old last one is no longer last).
        Assert.Throws<BundleException>(() => BundleFormat.Read(Assemble([.. chunks, chunks[3]]), Passphrase));
    }

    [Fact]
    public async Task WeakKeyDerivation_InAHeader_IsRefused()
    {
        var bundle = await WriteAsync([1, 2, 3]);
        var text = System.Text.Encoding.UTF8.GetString(bundle);
        var weakened = System.Text.Encoding.UTF8.GetBytes(text.Replace("\"iterations\":100000", "\"iterations\":     1", StringComparison.Ordinal));

        var error = Assert.Throws<BundleException>(() => BundleFormat.Read(weakened, Passphrase));
        Assert.Contains("encryption settings", error.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> WriteAsync(byte[] plaintext)
    {
        using var output = new MemoryStream();
        var header = new BundleHeader(0, "test", "migration", DateTimeOffset.UtcNow, "tester", "", 0, "", "", 0);
        await BundleFormat.WriteAsync(output, header, Passphrase, plaintext, Ct, iterations: 100_000);
        return output.ToArray();
    }

    /// <summary>Splits a bundle into its chunks (length, ciphertext and tag each), after the magic and header.</summary>
    private static List<byte[]> Chunks(byte[] bundle, out int prefixLength)
    {
        var offset = 8 + 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(8));
        prefixLength = offset;
        var chunks = new List<byte[]>();
        while (offset < bundle.Length)
        {
            var size = 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(offset)) + 16;
            chunks.Add(bundle[offset..(offset + size)]);
            offset += size;
        }

        return chunks;
    }
}
