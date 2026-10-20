using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NKeeper.Infrastructure.Auditing;

namespace NKeeper.Api.Auditing;

/// <summary>
/// Records what a terminal showed as an asciicast v2 file (the format asciinema plays): a JSON
/// header line, then one <c>[seconds, "o", text]</c> line per output and <c>[seconds, "r", "COLSxROWS"]</c>
/// per resize. Only output is recorded, never what was typed. It's compressed as it goes, and stops
/// (marking itself truncated) at a size limit, so a session that floods the terminal can't exhaust memory.
/// </summary>
public sealed class TerminalRecorder : IDisposable
{
    private readonly MemoryStream _compressed = new();
    private readonly GZipStream _gzip;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly DateTimeOffset _started;
    private readonly long _limit;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private long _size;
    private bool _truncated;

    internal TerminalRecorder(int cols, int rows, DateTimeOffset started, string title, long limit, TimeProvider time)
    {
        _started = started;
        _limit = limit;
        _time = time;
        _gzip = new GZipStream(_compressed, CompressionLevel.Fastest, leaveOpen: true);
        Write(JsonSerializer.Serialize(new { version = 2, width = cols, height = rows, timestamp = started.ToUnixTimeSeconds(), title }));
    }

    /// <summary>Terminal output; multi-byte characters split between chunks are joined up.</summary>
    public void Output(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var chars = new char[_decoder.GetCharCount(data, flush: false)];
            var count = _decoder.GetChars(data, chars, flush: false);
            if (count > 0)
            {
                Event("o", new string(chars, 0, count));
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            Event("r", $"{cols}x{rows}");
        }
    }

    internal SessionRecording Finish(string sessionId, Guid? userId, Guid? hostId, string protocol, DateTimeOffset ended)
    {
        lock (_lock)
        {
            _gzip.Dispose();
            return new SessionRecording
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                UserId = userId,
                HostId = hostId,
                Protocol = protocol,
                StartedAtUtc = _started,
                EndedAtUtc = ended,
                Data = _compressed.ToArray(),
                Size = _size,
                Truncated = _truncated,
            };
        }
    }

    public void Dispose()
    {
        _gzip.Dispose();
        _compressed.Dispose();
    }

    private void Event(string kind, string text)
    {
        var seconds = Math.Round((_time.GetUtcNow() - _started).TotalSeconds, 3);
        Write(JsonSerializer.Serialize(new object[] { seconds, kind, text }));
    }

    private void Write(string line)
    {
        if (_truncated)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        if (_size + bytes.Length > _limit)
        {
            _truncated = true;
            return;
        }

        _size += bytes.Length;
        _gzip.Write(bytes);
    }
}
