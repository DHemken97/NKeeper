using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NKeeper.Api.Access;
using NKeeper.Api.Auditing;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Security;
using NKeeper.Api.Ssh;
using NKeeper.Infrastructure.Inventory;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace NKeeper.Api.Controllers;

/// <summary>An SFTP request: which host, SSH service and login, and the SSH host key the user chose to trust.</summary>
public record SftpRequest : SessionStartMessage
{
    [StringLength(128)]
    public string? TrustFingerprint { get; init; }

    /// <summary>A folder or file on the host; empty means the login's home folder.</summary>
    [StringLength(4096)]
    public string? Path { get; init; }
}

public sealed record SftpRenameRequest : SftpRequest
{
    [Required(ErrorMessage = "Enter the new name.")]
    [StringLength(4096)]
    public string NewPath { get; init; } = string.Empty;
}

/// <summary>An upload: the same login fields as a form, the folder to put the files in, and the files.</summary>
public sealed class SftpUploadForm
{
    public Guid HostId { get; init; }

    [Range(0, 100)]
    public int ServiceIndex { get; init; }

    public Guid? CredentialId { get; init; }

    [StringLength(256)]
    public string? Username { get; init; }

    [StringLength(1024)]
    public string? Password { get; init; }

    [Required]
    [StringLength(4096)]
    public string Directory { get; init; } = string.Empty;

    /// <summary>Replace files that already exist; otherwise they're refused.</summary>
    public bool Overwrite { get; init; }

    [Required]
    public List<IFormFile> Files { get; init; } = [];
}

/// <param name="Kind">"directory", "file", "link" or "other".</param>
/// <param name="Permissions">e.g. "rwxr-xr-x".</param>
public sealed record SftpEntryResponse(string Name, string Path, string Kind, long Size, DateTimeOffset ModifiedUtc, string Permissions);

/// <param name="Status">"ready", or "trustRequired" when the SSH host key must be confirmed first.</param>
/// <param name="Parent">The folder above, or null at the root.</param>
public sealed record SftpListResponse(string Status, string? Path, string? Parent, IReadOnlyList<SftpEntryResponse>? Entries, HostKeyPromptResponse? HostKey);

/// <param name="Size">The bytes in the files in the folder and the folders below it.</param>
/// <param name="Complete">False when part of the folder couldn't be read, so it's at least this big.</param>
public sealed record SftpFolderSizeResponse(long Size, bool Complete);

public sealed record SftpDownloadLinkResponse(string Url);

/// <summary>
/// Files on hosts over SFTP, through the host's SSH service (and jump host), with the SSH host key
/// checked like SSH sessions do. Each request is its own short SFTP connection; downloads stream
/// through a one-time link so large files never sit in the browser's memory. Everything that reads
/// or changes a file is audited.
/// </summary>
[ApiController]
[Authorize]
[Route("api/sftp")]
public sealed class SftpController(SftpDownloadLinks links, SftpTempSpace tempSpace, AuditLog audit) : ControllerBase
{
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Kept free beyond a folder's size when zipping it: a zip adds a little per file, and the server needs room to carry on.</summary>
    private const long ZipHeadroomBytes = 100L * 1024 * 1024;

    /// <summary>Not real files: /proc/kcore alone reports over 100 TB.</summary>
    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal) { "/proc", "/sys", "/dev" };

    [HttpPost("list")]
    [ProducesResponseType<SftpListResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SftpListResponse>> List(SftpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            var path = Resolve(client, request.Path);
            var entries = new List<SftpEntryResponse>();
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken))
            {
                if (file.Name is not ("." or ".."))
                {
                    entries.Add(ToResponse(file));
                }
            }

            entries.Sort((a, b) => (a.Kind == "directory") != (b.Kind == "directory")
                ? a.Kind == "directory" ? -1 : 1
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new SftpListResponse("ready", path, ParentOf(path), entries, null);
        }
        catch (HostKeyTrustRequiredException trust)
        {
            return new SftpListResponse("trustRequired", null, null, null, new HostKeyPromptResponse(trust.Algorithm, trust.Fingerprint));
        }
        catch (SessionException ex)
        {
            return this.SessionFailure(ex);
        }
    }

    /// <summary>
    /// Adds up everything below a folder, which can take a while on a big one; it stops if the browser
    /// stops waiting. Symbolic links aren't followed. Folders that can't be read are left out and reported.
    /// </summary>
    [HttpPost("folder-size")]
    [ProducesResponseType<SftpFolderSizeResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SftpFolderSizeResponse>> FolderSize(SftpRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Choose a folder.");
        }

        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            return await MeasureFolderAsync(client, Resolve(client, request.Path), cancellationToken);
        }
        catch (Exception ex) when (ex is SessionException or HostKeyTrustRequiredException)
        {
            return this.SessionFailure(ex as SessionException ?? TrustFirst());
        }
    }

    /// <summary>
    /// A one-time link to download a file or folder (valid for a minute, for this user only). A folder is
    /// checked against the server's free space here, where the browser can show why it was refused.
    /// </summary>
    [HttpPost("download-link")]
    [ProducesResponseType<SftpDownloadLinkResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SftpDownloadLinkResponse>> DownloadLink(SftpRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Choose a file or folder to download.");
        }

        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            var path = Resolve(client, request.Path);
            if (client.GetAttributes(path).IsDirectory)
            {
                var folder = await MeasureFolderAsync(client, path, cancellationToken);
                var free = tempSpace.AvailableBytes;
                if (folder.Size + ZipHeadroomBytes > free)
                {
                    throw new SessionException(
                        SessionErrorCodes.FileOperationFailed,
                        $"{path} is too big to zip: it holds {Megabytes(folder.Size)} and the NKeeper server has {Megabytes(free)} free. Download smaller folders.");
                }
            }
        }
        catch (Exception ex) when (ex is SessionException or HostKeyTrustRequiredException)
        {
            return this.SessionFailure(ex as SessionException ?? TrustFirst());
        }
        catch (SshException ex)
        {
            return this.SessionFailure(Translate(ex, request.Path));
        }

        return new SftpDownloadLinkResponse($"/api/sftp/download/{links.Create(User.GetUserId(), request)}");
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024 * 1024):N0} MB";

    [HttpGet("download/{token}")]
    public async Task<IActionResult> Download(string token, CancellationToken cancellationToken)
    {
        if (links.Take(token, User.GetUserId()) is not { } request)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, detail: "This download link has expired or was already used. Download the file again.");
        }

        SftpClient? client = null;
        try
        {
            client = await ConnectAsync(request, cancellationToken);
            var path = Resolve(client, request.Path);
            var attributes = client.GetAttributes(path);
            if (attributes.IsDirectory)
            {
                return await DownloadFolderAsync(client, request.HostId, path, cancellationToken);
            }

            var stream = client.OpenRead(path);
            HttpContext.Response.RegisterForDispose(stream);
            HttpContext.Response.RegisterForDispose(client);
            client = null;
            await audit.RecordAsync(new AuditEntry("sftp.download", request.HostId, $"{path} ({attributes.Size:N0} bytes)"));
            Response.Headers.CacheControl = "no-store";
            return File(stream, "application/octet-stream", System.IO.Path.GetFileName(path), enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is SessionException or HostKeyTrustRequiredException)
        {
            return this.SessionFailure(ex as SessionException ?? TrustFirst());
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// A folder as a zip, built in this server's temp folder and deleted as soon as the response ends,
    /// however it ends. The audit log records the folder on the host.
    /// </summary>
    private async Task<IActionResult> DownloadFolderAsync(SftpClient client, Guid hostId, string path, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var zip = new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nkeeper-sftp-{Guid.NewGuid():N}.zip"), options);
        HttpContext.Response.RegisterForDispose(zip);
        long size;
        int skipped;
        try
        {
            (size, skipped) = await WriteZipAsync(client, path, zip, cancellationToken);
        }
        catch (SshException ex)
        {
            throw Translate(ex, path);
        }
        catch (IOException ex)
        {
            throw new SessionException(SessionErrorCodes.FileOperationFailed, $"The zip couldn't be made on the NKeeper server: {ex.Message}");
        }

        zip.Position = 0;
        var left = skipped > 0 ? $", {skipped:N0} unreadable left out" : string.Empty;
        await audit.RecordAsync(new AuditEntry("sftp.download", hostId, $"{path} (folder, {size:N0} bytes{left})"));
        Response.Headers.CacheControl = "no-store";
        var name = path == "/" ? "root" : System.IO.Path.GetFileName(path);
        return File(zip, "application/zip", $"{name}.zip", enableRangeProcessing: false);
    }

    /// <summary>Zips everything below a folder. What can't be read is left out, and listed in a file in the zip so it's not silently missing.</summary>
    private static async Task<(long Size, int Skipped)> WriteZipAsync(SftpClient client, string root, Stream output, CancellationToken cancellationToken)
    {
        var size = 0L;
        var skipped = new List<string>();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var folders = new Stack<string>([root]);
        while (folders.TryPop(out var folder))
        {
            if (await ReadFolderAsync(client, folder, cancellationToken) is not { } files)
            {
                skipped.Add(folder);
                continue;
            }

            if (files.Count == 0 && folder != root)
            {
                zip.CreateEntry($"{ZipName(root, folder)}/");
            }

            foreach (var file in files)
            {
                if (file.IsDirectory)
                {
                    folders.Push(file.FullName);
                }
                else if (file.IsRegularFile)
                {
                    try
                    {
                        await using var source = await client.OpenAsync(file.FullName, FileMode.Open, FileAccess.Read, cancellationToken);
                        var entry = zip.CreateEntry(ZipName(root, file.FullName), CompressionLevel.Fastest);

                        // A zip can't hold dates outside these years, and old timestamps do turn up.
                        if (file.LastWriteTimeUtc.Year is >= 1980 and <= 2107)
                        {
                            entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc));
                        }

                        await using var target = entry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                        size += file.Length;
                    }
                    catch (SshException ex) when (ex is not SshConnectionException)
                    {
                        skipped.Add(file.FullName);
                    }
                }
            }
        }

        if (skipped.Count > 0)
        {
            await using var note = new StreamWriter(zip.CreateEntry("_nkeeper-unreadable.txt").Open(), Encoding.UTF8);
            await note.WriteLineAsync($"These couldn't be read from {root}, so they are missing or incomplete in this zip:");
            foreach (var path in skipped)
            {
                await note.WriteLineAsync(path);
            }
        }

        return (size, skipped.Count);
    }

    /// <summary>The path inside the zip. A backslash is legal in a name here but a separator to some unzip tools, which could climb out of the folder.</summary>
    private static string ZipName(string root, string path) => path[(root.TrimEnd('/').Length + 1)..].Replace('\\', '_');

    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType<SftpListResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SftpListResponse>> Upload([FromForm] SftpUploadForm form, CancellationToken cancellationToken)
    {
        var request = new SftpRequest
        {
            HostId = form.HostId, ServiceIndex = form.ServiceIndex, CredentialId = form.CredentialId, Username = form.Username, Password = form.Password, Path = form.Directory,
        };
        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            var directory = Resolve(client, form.Directory);
            foreach (var file in form.Files)
            {
                // Only the file's own name: a crafted name can't climb out of the folder.
                var name = System.IO.Path.GetFileName(file.FileName.Replace('\\', '/'));
                if (name.Length == 0 || name is "." or "..")
                {
                    throw new SessionException(SessionErrorCodes.FileOperationFailed, $"“{file.FileName}” isn't a valid file name.");
                }

                var path = Join(directory, name);
                if (!form.Overwrite && client.Exists(path))
                {
                    throw new SessionException(SessionErrorCodes.FileExists, $"“{name}” already exists in {directory}.");
                }

                await using var source = file.OpenReadStream();
                await using (var target = client.Open(path, FileMode.Create, FileAccess.Write))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                await audit.RecordAsync(new AuditEntry("sftp.upload", request.HostId, $"{path} ({file.Length:N0} bytes)"));
            }

            return new SftpListResponse("ready", directory, ParentOf(directory), null, null);
        }
        catch (Exception ex) when (ex is SessionException or HostKeyTrustRequiredException)
        {
            return this.SessionFailure(ex as SessionException ?? TrustFirst());
        }
    }

    [HttpPost("mkdir")]
    public Task<ActionResult<SftpListResponse>> CreateFolder(SftpRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(request, "sftp.mkdir", (client, path) => client.CreateDirectory(path), cancellationToken, mustExist: false);

    /// <summary>Deletes a file, or an empty folder (never a folder's contents).</summary>
    [HttpPost("delete")]
    public Task<ActionResult<SftpListResponse>> Delete(SftpRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(request, "sftp.delete", (client, path) =>
        {
            if (client.GetAttributes(path).IsDirectory)
            {
                if (client.ListDirectory(path).Any(f => f.Name is not ("." or "..")))
                {
                    throw new SessionException(SessionErrorCodes.FileOperationFailed, $"{path} isn't empty. Delete what's in it first.");
                }

                client.DeleteDirectory(path);
            }
            else
            {
                client.DeleteFile(path);
            }
        }, cancellationToken);

    [HttpPost("rename")]
    public Task<ActionResult<SftpListResponse>> Rename(SftpRenameRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(request, "sftp.rename", (client, path) =>
        {
            var target = request.NewPath.StartsWith('/') ? request.NewPath : Join(ParentOf(path) ?? "/", request.NewPath);
            client.RenameFile(path, target);
        }, cancellationToken, $" to {request.NewPath}");

    private async Task<ActionResult<SftpListResponse>> ChangeAsync(
        SftpRequest request, string action, Action<SftpClient, string> change, CancellationToken cancellationToken, string? detail = null, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Choose a file or folder.");
        }

        try
        {
            using var client = await ConnectAsync(request, cancellationToken);
            var path = Resolve(client, request.Path, mustExist);
            try
            {
                change(client, path);
            }
            catch (SshException ex)
            {
                throw Translate(ex, path);
            }

            await audit.RecordAsync(new AuditEntry(action, request.HostId, path + detail));
            return new SftpListResponse("ready", path, ParentOf(path), null, null);
        }
        catch (Exception ex) when (ex is SessionException or HostKeyTrustRequiredException)
        {
            return this.SessionFailure(ex as SessionException ?? TrustFirst());
        }
    }

    private async Task<SftpClient> ConnectAsync(SftpRequest request, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var target = await services.GetRequiredService<SessionTargetResolver>()
            .ResolveAsync(request, RequestUser.From(User), ServiceProtocol.Ssh, 22, cancellationToken);
        return await SshCommandConnection.ConnectSftpAsync(target, request.TrustFingerprint, User.GetUserId(), services.GetRequiredService<TrustedKeyStore>(), cancellationToken);
    }

    /// <summary>The bytes in the files below a folder that can be read.</summary>
    private static async Task<SftpFolderSizeResponse> MeasureFolderAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        var size = 0L;
        var complete = true;
        var folders = new Stack<string>([path]);
        try
        {
            while (folders.TryPop(out var folder))
            {
                if (await ReadFolderAsync(client, folder, cancellationToken) is not { } files)
                {
                    complete = false;
                    continue;
                }

                foreach (var file in files)
                {
                    if (file.IsDirectory)
                    {
                        folders.Push(file.FullName);
                    }
                    else if (file.IsRegularFile)
                    {
                        size += file.Length;
                    }
                }
            }
        }
        catch (SshException ex)
        {
            throw Translate(ex, path);
        }

        return new SftpFolderSizeResponse(size, complete);
    }

    /// <summary>What's in a folder, or null if it can't be read or isn't real files. A lost connection still throws.</summary>
    private static async Task<List<ISftpFile>?> ReadFolderAsync(SftpClient client, string folder, CancellationToken cancellationToken)
    {
        if (PseudoFileSystems.Contains(folder))
        {
            return null;
        }

        try
        {
            var files = new List<ISftpFile>();
            await foreach (var file in client.ListDirectoryAsync(folder, cancellationToken))
            {
                if (file.Name is not ("." or ".."))
                {
                    files.Add(file);
                }
            }

            return files;
        }
        catch (SshException ex) when (ex is not SshConnectionException)
        {
            return null;
        }
    }

    /// <summary>The path as an absolute one (relative paths are from the home folder), checked to exist unless it's about to be created.</summary>
    private static string Resolve(SftpClient client, string? path, bool mustExist = true)
    {
        var resolved = Normalize(string.IsNullOrWhiteSpace(path) ? client.WorkingDirectory : path.StartsWith('/') ? path : Join(client.WorkingDirectory, path));
        if (!mustExist)
        {
            return resolved;
        }

        try
        {
            client.GetAttributes(resolved);
            return resolved;
        }
        catch (SshException ex)
        {
            throw Translate(ex, resolved);
        }
    }

    private static SessionException Translate(SshException ex, string path) => ex switch
    {
        SftpPathNotFoundException => new SessionException(SessionErrorCodes.FileOperationFailed, $"{path} doesn't exist (any more)."),
        SftpPermissionDeniedException => new SessionException(SessionErrorCodes.FileOperationFailed, $"The account isn't allowed to do that with {path}."),
        _ => new SessionException(SessionErrorCodes.FileOperationFailed, $"The server refused: {ex.Message}"),
    };

    private static SessionException TrustFirst() =>
        new(SessionErrorCodes.HostKeyRejected, "Open the host's files first, to check and trust its SSH host key.");

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return "/" + string.Join('/', parts);
    }

    private static string Join(string directory, string name) => Normalize($"{directory.TrimEnd('/')}/{name}");

    private static string? ParentOf(string path) => path == "/" ? null : Normalize(path + "/..");

    private static SftpEntryResponse ToResponse(ISftpFile file) => new(
        file.Name,
        file.FullName,
        file.IsDirectory ? "directory" : file.IsRegularFile ? "file" : file.IsSymbolicLink ? "link" : "other",
        file.IsDirectory ? 0 : file.Length,
        new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
        Permissions(file.Attributes));

    private static string Permissions(SftpFileAttributes a) => string.Concat(
        a.OwnerCanRead ? 'r' : '-', a.OwnerCanWrite ? 'w' : '-', a.OwnerCanExecute ? 'x' : '-',
        a.GroupCanRead ? 'r' : '-', a.GroupCanWrite ? 'w' : '-', a.GroupCanExecute ? 'x' : '-',
        a.OthersCanRead ? 'r' : '-', a.OthersCanWrite ? 'w' : '-', a.OthersCanExecute ? 'x' : '-');
}

/// <summary>The free space where folder downloads are zipped.</summary>
public class SftpTempSpace
{
    public virtual long AvailableBytes => new DriveInfo(System.IO.Path.GetTempPath()).AvailableFreeSpace;
}

/// <summary>
/// One-time download links: the request (including a one-off password, in memory only) for a minute,
/// redeemable once by the user who asked for it.
/// </summary>
public sealed class SftpDownloadLinks(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (Guid UserId, SftpRequest Request, DateTimeOffset Expires)> _links = new(StringComparer.Ordinal);

    public string Create(Guid userId, SftpRequest request)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in _links.Where(l => l.Value.Expires < now).Select(l => l.Key).ToList())
        {
            _links.TryRemove(expired, out _);
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _links[token] = (userId, request, now + Lifetime);
        return token;
    }

    public SftpRequest? Take(string token, Guid userId) =>
        _links.TryRemove(token, out var link) && link.UserId == userId && link.Expires >= timeProvider.GetUtcNow() ? link.Request : null;
}
