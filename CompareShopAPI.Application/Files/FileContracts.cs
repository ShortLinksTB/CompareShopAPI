using CompareShopAPI.Domain.Files;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Application.Files;

public static class FilePermissions
{
    public const string Read = CompareShopAPI.Application.Admin.AdminPermissions.FilesRead;
    public const string Write = CompareShopAPI.Application.Admin.AdminPermissions.FilesWrite;
}

public sealed record FileMetadata(Guid Id, string OriginalFileName, string ContentType, long SizeBytes,
    int? Width, int? Height, string UploadedBy, DateTime UploadedAtUtc);
public sealed record FilePage(IReadOnlyList<FileMetadata> Items, int Page, int PageSize, int TotalItems, int TotalPages);
public sealed record FileUpload(string FileName, string ContentType, long SizeBytes, Stream Content);
public sealed record FileUploadOutcome(string FileName, FileMetadata? File, int StatusCode, string? Error)
{
    public bool Succeeded => File is not null;
}
public interface IFileRepository
{
    Task<FilePage> List(int page, int pageSize, string? query, string? type, CancellationToken ct);
    Task<FileAsset?> Get(Guid id, CancellationToken ct);
    Task Add(FileAsset file, CancellationToken ct);
    Task<bool> Delete(Guid id, CancellationToken ct);
}

public interface IFileObjectStorage
{
    string PublicUrl(string key);
    Task Put(string key, Stream content, long size, string contentType, CancellationToken ct);
    Task Delete(string key, CancellationToken ct);
}

public interface IFilePermissionGate
{
    Task<bool> HasPermission(string subject, string permission, CancellationToken ct);
}

public sealed class FileService(IFileRepository files, IFileObjectStorage storage, IFilePermissionGate permissions,
    TimeProvider clock, ILogger<FileService> logger)
{
    public async Task<FilePage> List(string? subject, int page, int pageSize, string? query, string? type, CancellationToken ct)
    {
        await Authorize(subject, FilePermissions.Read, ct);
        ValidatePage(page, pageSize);
        var normalizedType = type?.Trim().ToLowerInvariant();
        if (normalizedType is not null && normalizedType is not ("images" or "other")) throw new ArgumentException("Type must be 'images' or 'other'.");
        return await files.List(page, pageSize, query?.Trim(), normalizedType, ct);
    }

    public async Task<FileMetadata?> Get(string? subject, Guid id, CancellationToken ct)
    {
        await Authorize(subject, FilePermissions.Read, ct);
        ValidateId(id);
        var file = await files.Get(id, ct);
        return file is null ? null : ToMetadata(file);
    }

    public async Task<FileMetadata> Upload(string? subject, FileUpload upload, CancellationToken ct)
    {
        var actor = await Authorize(subject, FilePermissions.Write, ct);
        ValidateUpload(upload);
        var contentType = upload.ContentType.Trim().ToLowerInvariant();
        var originalFileName = Path.GetFileName(upload.FileName.Replace('\\', '/'));
        var id = Guid.NewGuid();
        var key = $"{id:N}";
        var file = new FileAsset(id, originalFileName, contentType, upload.SizeBytes, key,
            actor, clock.GetUtcNow().UtcDateTime);
        await storage.Put(key, upload.Content, upload.SizeBytes, contentType, ct);
        try
        {
            await files.Add(file, ct);
        }
        catch
        {
            try { await storage.Delete(key, CancellationToken.None); }
            catch (Exception cleanupError) { logger.LogError(cleanupError, "Failed to clean up orphaned file object {FileId}", id); }
            throw;
        }
        return ToMetadata(file);
    }

    public async Task<bool> Delete(string? subject, Guid id, CancellationToken ct)
    {
        await Authorize(subject, FilePermissions.Write, ct);
        ValidateId(id);
        var file = await files.Get(id, ct);
        if (file is null) return false;
        if (!await files.Delete(id, ct)) return false;
        await storage.Delete(file.ObjectKey, ct);
        return true;
    }

    public async Task<string?> PublicObjectUrl(Guid id, CancellationToken ct)
    {
        ValidateId(id);
        if (await files.Get(id, ct) is not { } file) return null;
        return storage.PublicUrl(file.ObjectKey);
    }

    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new CompareShopAPI.Application.Admin.AdminUnauthorizedException();
        if (!await permissions.HasPermission(subject, permission, ct)) throw new CompareShopAPI.Application.Admin.AdminForbiddenException();
        return subject;
    }

    private static void ValidateUpload(FileUpload upload)
    {
        if (upload.SizeBytes is <= 0 or > FileUploadPolicy.MaxBytes) throw new ArgumentException("File size must be between 1 byte and 10 MiB.");
        if (!FileUploadPolicy.AllowedContentTypes.Contains(upload.ContentType)) throw new ArgumentException("Unsupported file type.");
        var name = Path.GetFileName(upload.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)) throw new ArgumentException("Invalid filename.");
    }
    private static void ValidateId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Invalid file ID."); }
    private static void ValidatePage(int page, int size) { if (page < 1 || size is < 1 or > 100) throw new ArgumentException("Page must be positive and pageSize must be between 1 and 100."); }
    private static FileMetadata ToMetadata(FileAsset file) => new(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Width, file.Height, file.UploadedBy, file.UploadedAtUtc);
}
