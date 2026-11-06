using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NKeeper.Api.Configuration;

namespace NKeeper.Api.Diagnostics;

/// <summary>The diagnostics file's settings (section <c>Diagnostics</c>). The rest are on the Settings page.</summary>
public sealed class DiagnosticsSettings
{
    public const string SectionName = "Diagnostics";

    /// <summary>Where the file is kept. Must survive upgrades; the setup scripts set it for each kind of install.</summary>
    public string? Directory { get; set; }
}

/// <summary>The free space where the diagnostics file is kept.</summary>
public class DiagnosticsDisk
{
    public virtual long AvailableBytes(string directory) => new DriveInfo(directory).AvailableFreeSpace;
}

/// <summary>What Settings ▸ Diagnostics shows.</summary>
public sealed record DiagnosticsStatus(
    bool Enabled,
    int MaxMegabytes,
    long SizeBytes,
    DateTimeOffset? OldestUtc,
    DateTimeOffset? NewestUtc,
    DateTimeOffset? LastDownloadedUtc,
    DateTimeOffset? DroppedUtc,
    bool PausedForDiskSpace,
    string? WriteError,
    string Directory);

/// <summary>
/// A file on the server's disk (one JSON object per line) for finding bugs and usage patterns away
/// from the server. Errors are written as they happen, the same error only once a day with a count;
/// see <see cref="DiagnosticsMonitor"/> for the rest. A current and an older file share the size
/// limit: when the current one is full it replaces the older one, so the oldest data goes first.
/// Nothing here may ever break what's being recorded, so failures only show on the Settings page.
/// It can't log them either: logging is one of its sources.
/// </summary>
public sealed class DiagnosticsLog : IDisposable
{
    public const int MinMegabytes = 20;
    public const int CeilingMegabytes = 10_240;
    public const int DefaultMegabytes = 200;
    public const string CurrentFileName = "diagnostics.ndjson";
    public const string OlderFileName = "diagnostics-older.ndjson";
    private const string StateFileName = "state.json";
    private const int Schema = 1;
    private const int MaxFingerprints = 10_000;
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly DiagnosticsDisk _disk;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Repeated> _errors = new(StringComparer.Ordinal);
    private readonly DiagnosticsRedactor _redactor;
    private State _state = new();
    private FileStream? _stream;
    private long _reserveBytes = 5L * 1024 * 1024 * 1024;
    private DateTimeOffset _nextDiskCheck;
    private DateTimeOffset? _pausedSince;
    private string? _writeError;
    private bool _disposed;

    public DiagnosticsLog(IOptions<DiagnosticsSettings> settings, DiagnosticsDisk disk, TimeProvider timeProvider)
    {
        _directory = ResolveDirectory(settings.Value.Directory);
        _disk = disk;
        _time = timeProvider;
        LoadState();
        _redactor = new DiagnosticsRedactor(Convert.FromBase64String(_state.Secret));
        Usage = new DiagnosticsUsage(this, _redactor, timeProvider);
    }

    public bool Enabled => _state.Enabled;

    public DiagnosticsUsage Usage { get; }

    internal DiagnosticsRedactor Redactor => _redactor;

    /// <summary>Writing pauses below this much free disk space (Settings ▸ Limits); 0 never pauses.</summary>
    public void SetReserve(long bytes)
    {
        lock (_gate)
        {
            _reserveBytes = bytes;
            _nextDiskCheck = default;
        }
    }

    /// <summary>Adds one line. The payload's properties follow its time and type.</summary>
    public void Write(string type, object payload)
    {
        lock (_gate)
        {
            WriteLocked(type, payload);
        }
    }

    /// <summary>Records an error, once a day however often it repeats; <see cref="FlushRepeats"/> adds the counts.</summary>
    /// <param name="area">Where it comes from: a log category, or "browser". Never free text.</param>
    /// <param name="message">Must be redacted already, or a message template.</param>
    internal void RecordError(string area, string level, string message, string? exception, string? stack, object? context = null)
    {
        var firstFrame = stack?.Split('\n', 2)[0];
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{area}\n{message}\n{exception?.Split(':', 2)[0]}\n{firstFrame}")).AsSpan(0, 6));
        lock (_gate)
        {
            if (!_state.Enabled || _disposed)
            {
                return;
            }

            if (_errors.TryGetValue(fingerprint, out var seen))
            {
                seen.Count++;
                seen.LastUtc = _time.GetUtcNow();
                return;
            }

            if (_errors.Count >= MaxFingerprints)
            {
                FlushRepeatsLocked(forget: true);
            }

            _errors[fingerprint] = new Repeated(area);
            WriteLocked("error", new { area, level, fingerprint, message, exception, stack, context });
        }
    }

    /// <summary>Adds how often errors repeated since the last time.</summary>
    /// <param name="forget">At the end of the day: the next occurrence is written in full, so older files going doesn't lose every copy.</param>
    public void FlushRepeats(bool forget = false)
    {
        lock (_gate)
        {
            FlushRepeatsLocked(forget);
        }
    }

    public void SetKnownNames(IEnumerable<string> names)
    {
        var known = names.ToList();
        lock (_gate)
        {
            _redactor.SetKnownNames(known);
        }
    }

    public DiagnosticsStatus Status()
    {
        lock (_gate)
        {
            var older = FileIn(OlderFileName);
            var current = FileIn(CurrentFileName);
            var size = Length(older) + Length(current);
            return new DiagnosticsStatus(
                _state.Enabled,
                _state.MaxMegabytes,
                size,
                FirstTime(older) ?? FirstTime(current),
                File.Exists(current) ? new DateTimeOffset(File.GetLastWriteTimeUtc(current), TimeSpan.Zero) : null,
                _state.LastDownloadedUtc,
                _state.DroppedUtc,
                _pausedSince is not null,
                _writeError,
                _directory);
        }
    }

    public void Update(bool enabled, int maxMegabytes)
    {
        lock (_gate)
        {
            var wasEnabled = _state.Enabled;
            _state.Enabled = enabled;
            _state.MaxMegabytes = maxMegabytes;
            _writeError = null;
            SaveState();
            if (!enabled)
            {
                CloseStream();
                _pausedSince = null;
                return;
            }

            try
            {
                EnforceLimit();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writeError = ex.Message;
            }

            WriteLocked(wasEnabled ? "settings" : "enabled", new { maxMegabytes });
        }
    }

    /// <summary>Writes the files as a zip with a manifest, and notes the download once it has all gone out.</summary>
    public async Task WriteZipAsync(Stream output, CancellationToken cancellationToken)
    {
        // Opened (and measured, so no half-written line) together, so a rotation can't split or repeat data.
        var sources = new List<(string Name, FileStream Stream, long Length)>();
        object manifest;
        try
        {
            lock (_gate)
            {
                foreach (var name in new[] { OlderFileName, CurrentFileName })
                {
                    if (File.Exists(FileIn(name)))
                    {
                        var source = new FileStream(FileIn(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920, useAsync: true);
                        sources.Add((name, source, source.Length));
                    }
                }

                manifest = new
                {
                    schema = Schema,
                    generatedUtc = _time.GetUtcNow(),
                    version = AppVersion.Full,
                    enabled = _state.Enabled,
                    maxMegabytes = _state.MaxMegabytes,
                    previousDownloadUtc = _state.LastDownloadedUtc,
                    droppedSincePreviousDownloadUtc = _state.DroppedUtc,
                    pausedSinceUtc = _pausedSince,
                    files = sources.Select(s => new { name = s.Name, bytes = s.Length }),
                };
            }

            await using (var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
            {
                await using (var entry = await zip.CreateEntry("manifest.json").OpenAsync(cancellationToken))
                {
                    await entry.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(manifest, Json), cancellationToken);
                }

                foreach (var (name, source, length) in sources)
                {
                    await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken);
                    await CopyAsync(source, entry, length, cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var (_, source, _) in sources)
            {
                await source.DisposeAsync();
            }
        }

        lock (_gate)
        {
            _state.LastDownloadedUtc = _time.GetUtcNow();
            _state.DroppedUtc = null;
            SaveState();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseStream();
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        for (var left = length; left > 0;)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            left -= read;
        }
    }

    private static string ResolveDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local.Length > 0 ? local : Path.GetTempPath(), "NKeeper", "diagnostics");
    }

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static DateTimeOffset? FirstTime(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadLine() is { } line && JsonNode.Parse(line)?["t"]?.GetValue<DateTimeOffset>() is { } time ? time : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private string FileIn(string name) => Path.Combine(_directory, name);

    private void WriteLocked(string type, object payload)
    {
        if (!_state.Enabled || _disposed || !HasDiskSpace())
        {
            return;
        }

        var now = _time.GetUtcNow();
        var line = new JsonObject { ["t"] = now, ["type"] = type };
        if (_pausedSince is { } paused)
        {
            _pausedSince = null;
            WriteLocked("gap", new { fromUtc = paused, toUtc = now, reason = "low_disk_space" });
        }

        foreach (var (key, value) in JsonSerializer.SerializeToNode(payload, Json)!.AsObject().ToList())
        {
            line[key] = value?.DeepClone();
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line.ToJsonString(Json) + "\n");
            var stream = Stream();
            if (stream.Length + bytes.Length > PerFileBytes)
            {
                Rotate();
                stream = Stream();
            }

            stream.Write(bytes);
            stream.Flush();
            _writeError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CloseStream();
            _writeError = ex.Message;
        }
    }

    private long PerFileBytes => _state.MaxMegabytes * 1024L * 1024L / 2;

    private bool HasDiskSpace()
    {
        var now = _time.GetUtcNow();
        if (now < _nextDiskCheck)
        {
            return _pausedSince is null;
        }

        _nextDiskCheck = now + DiskCheckInterval;
        try
        {
            EnsureDirectory();
            if (_disk.AvailableBytes(_directory) >= _reserveBytes)
            {
                // WriteLocked says how long the pause was.
                return true;
            }

            _pausedSince ??= now;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // If it can't be measured, it isn't known to be low: a failed write will say so.
            return true;
        }
    }

    private FileStream Stream()
    {
        if (_stream is not null)
        {
            return _stream;
        }

        EnsureDirectory();
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return _stream = new FileStream(FileIn(CurrentFileName), options);
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Rotate()
    {
        CloseStream();
        DeleteOlder();
        File.Move(FileIn(CurrentFileName), FileIn(OlderFileName));
    }

    /// <summary>When the limit was lowered: what's over it goes now, oldest first.</summary>
    private void EnforceLimit()
    {
        if (Length(FileIn(CurrentFileName)) > PerFileBytes)
        {
            Rotate();
        }

        if (Length(FileIn(OlderFileName)) > PerFileBytes)
        {
            CloseStream();
            DeleteOlder();
        }
    }

    private void DeleteOlder()
    {
        var older = FileIn(OlderFileName);
        if (!File.Exists(older))
        {
            return;
        }

        var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(older), TimeSpan.Zero);
        File.Delete(older);
        if (_state.LastDownloadedUtc is not { } downloaded || lastWrite > downloaded)
        {
            _state.DroppedUtc = _time.GetUtcNow();
            SaveState();
        }
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private void FlushRepeatsLocked(bool forget)
    {
        foreach (var (fingerprint, repeated) in _errors.Where(e => e.Value.Count > 0))
        {
            WriteLocked("repeated", new { area = repeated.Area, fingerprint, count = repeated.Count, lastUtc = repeated.LastUtc });
            repeated.Count = 0;
        }

        if (forget)
        {
            _errors.Clear();
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(FileIn(StateFileName)) && JsonSerializer.Deserialize<State>(File.ReadAllText(FileIn(StateFileName)), Json) is { Secret.Length: > 0 } state)
            {
                _state = state;
                _state.MaxMegabytes = Math.Clamp(_state.MaxMegabytes, MinMegabytes, CeilingMegabytes);
                Convert.FromBase64String(_state.Secret);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _writeError = ex.Message;
            _state = new State();
        }

        _state.Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        SaveState();
    }

    /// <summary>Beside the data rather than in the database, so it still works when the database doesn't. Never in the download.</summary>
    private void SaveState()
    {
        try
        {
            EnsureDirectory();
            var temporary = FileIn(StateFileName + ".tmp");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(temporary, options))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(_state, Json));
            }

            File.Move(temporary, FileIn(StateFileName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _writeError = ex.Message;
        }
    }

    private sealed class Repeated(string area)
    {
        public string Area { get; } = area;

        public int Count { get; set; }

        public DateTimeOffset LastUtc { get; set; }
    }

    private sealed class State
    {
        public bool Enabled { get; set; } = true;

        public int MaxMegabytes { get; set; } = DefaultMegabytes;

        /// <summary>Keys the tokens that stand in for names and addresses.</summary>
        public string Secret { get; set; } = "";

        public DateTimeOffset? LastDownloadedUtc { get; set; }

        /// <summary>When older data went before it was downloaded.</summary>
        public DateTimeOffset? DroppedUtc { get; set; }
    }
}
