using CompareShopAPI.Domain.Files;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Application.Files;

public static class FilePermissions
{
    public const string Read = CompareShopAPI.Application.Admin.AdminPermissions.FilesRead;
    public const string Write = CompareShopAPI.Application.Admin.AdminPermissions.FilesWrite;
}

public sealed record DerivedFromMetadata(Guid Id, string OriginalFileName);
public sealed record DerivativeSpec(int Version, int Width, int Height, string Fit, double? FocalX, double? FocalY,
    string Background, string Format, int Quality);
public sealed record FileMetadata(Guid Id, string OriginalFileName, string ContentType, long SizeBytes,
    int? Width, int? Height, string UploadedBy, DateTime UploadedAtUtc,
    DerivedFromMetadata? DerivedFrom = null, string? DerivedPreset = null, DerivativeSpec? DerivativeSpec = null);
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
    Task ReserveDerivativeAndAdd(FileAsset file, string candidateName, CancellationToken ct);
    Task AuditDerivative(string actor, Guid sourceId, int variantIndex, string parameters, string? resultFileId, string? errorCode, CancellationToken ct);
    Task<bool> Delete(Guid id, CancellationToken ct);
}

public interface IFileObjectStorage
{
    string PublicUrl(string key);
    Task Put(string key, Stream content, long size, string contentType, CancellationToken ct);
    Task Delete(string key, CancellationToken ct);
    Task<StoredFileContent> OpenRead(string key, CancellationToken ct);
}

public sealed record StoredFileContent(Stream Content, long ContentLength);
public sealed record DerivativeVariant(int Width, int Height, string Fit, double? FocalX, double? FocalY,
    string Background, string Format, int Quality, string? Name);
public sealed record ProcessedDerivative(byte[] Bytes, int Width, int Height, string ContentType, string Extension, IReadOnlyList<string> Warnings);
public interface IImageDerivativeProcessor
{
    string? DetectSourceContentType(ReadOnlyMemory<byte> source);
    ProcessedDerivative Process(ReadOnlyMemory<byte> source, string requestedFormat, int width, int height, string fit,
        double focalX, double focalY, string background, int quality);
}
public sealed record DerivativeVariantOutcome(int Index, FileMetadata? File, string? PublicUrl, string? Error, IReadOnlyList<string> Warnings)
{
    public bool Succeeded => File is not null;
}
public sealed record DerivativeRequest(IReadOnlyList<DerivativeVariant> Variants);

public interface IFilePermissionGate
{
    Task<bool> HasPermission(string subject, string permission, CancellationToken ct);
}

public sealed class FileService(IFileRepository files, IFileObjectStorage storage, IFilePermissionGate permissions,
    TimeProvider clock, ILogger<FileService> logger, IImageDerivativeProcessor? imageProcessor = null)
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
        return file is null ? null : ToMetadata(file, file.DerivedFromFile?.OriginalFileName);
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

    public async Task EnsureWritePermission(string? subject, CancellationToken ct) =>
        _ = await Authorize(subject, FilePermissions.Write, ct);

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
    public async Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct)
    {
        var actor = await Authorize(subject, FilePermissions.Write, ct);
        ValidateId(id);
        if (request.Variants is null || request.Variants.Count is < 1 or > 5)
            throw new ArgumentException("Between 1 and 5 variants are required.");
        var source = await files.Get(id, ct) ?? throw new KeyNotFoundException();
        StoredFileContent stored;
        try { stored = await storage.OpenRead(source.ObjectKey, ct); }
        catch (FileObjectTooLargeException)
        {
            foreach (var (variant, index) in request.Variants.Select((value, index) => (value, index)))
                await TryAuditDerivative(actor, id, index, variant, "source_pixel_limit_exceeded", ct);
            return request.Variants.Select((_, i) => new DerivativeVariantOutcome(i, null, null, "source_pixel_limit_exceeded", [])).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (variant, index) in request.Variants.Select((value, index) => (value, index)))
                await TryAuditDerivative(actor, id, index, variant, "storage_failed", ct);
            return request.Variants.Select((_, i) => new DerivativeVariantOutcome(i, null, null, "storage_failed", [])).ToArray();
        }
        if (stored.ContentLength is <= 0 or > FileUploadPolicy.MaxBytes)
        {
            await stored.Content.DisposeAsync();
            foreach (var (variant, index) in request.Variants.Select((value, index) => (value, index)))
                await TryAuditDerivative(actor, id, index, variant, "source_pixel_limit_exceeded", ct);
            return request.Variants.Select((_, i) => new DerivativeVariantOutcome(i, null, null, "source_pixel_limit_exceeded", [])).ToArray();
        }
        byte[] sourceBytes;
        await using (stored.Content)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stored.Content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > FileUploadPolicy.MaxBytes)
                {
                    foreach (var (_, index) in request.Variants.Select((value, index) => (value, index)))
                        logger.LogInformation("File derivative completed for {SourceFileId}, variant {VariantIndex}, error={ErrorCode}", id, index, "source_pixel_limit_exceeded");
                    foreach (var (variant, index) in request.Variants.Select((value, index) => (value, index)))
                        await TryAuditDerivative(actor, id, index, variant, "source_pixel_limit_exceeded", ct);
                    return request.Variants.Select((_, i) => new DerivativeVariantOutcome(i, null, null, "source_pixel_limit_exceeded", [])).ToArray();
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            sourceBytes = buffer.ToArray();
        }
        var detectedContentType = imageProcessor?.DetectSourceContentType(sourceBytes);
        if (detectedContentType is null)
        {
            foreach (var (variant, index) in request.Variants.Select((value, index) => (value, index)))
                await TryAuditDerivative(actor, id, index, variant, "unsupported_source_type", ct);
            return request.Variants.Select((_, i) => new DerivativeVariantOutcome(i, null, null, "unsupported_source_type", [])).ToArray();
        }
        var results = new List<DerivativeVariantOutcome>(request.Variants.Count);
        for (var i = 0; i < request.Variants.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await CreateOne(source, sourceBytes, request.Variants[i], i, actor, ct);
                results.Add(result);
                logger.LogInformation("File derivative completed for {SourceFileId}, variant {VariantIndex}, success={Succeeded}", id, i, true);
            }
            catch (DerivativeFailure failure)
            {
                results.Add(new(i, null, null, failure.Code, []));
                await TryAuditDerivative(actor, id, i, request.Variants[i], failure.Code, ct);
                logger.LogInformation("File derivative completed for {SourceFileId}, variant {VariantIndex}, error={ErrorCode}", id, i, failure.Code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Derivative variant {VariantIndex} failed for source {FileId}", i, id);
                results.Add(new(i, null, null, "storage_failed", []));
                await TryAuditDerivative(actor, id, i, request.Variants[i], "storage_failed", ct);
                logger.LogInformation("File derivative completed for {SourceFileId}, variant {VariantIndex}, error={ErrorCode}", id, i, "storage_failed");
            }
        }
        return results;
    }

    private async Task TryAuditDerivative(string actor, Guid sourceId, int index, DerivativeVariant variant, string error, CancellationToken ct)
    {
        try { await files.AuditDerivative(actor, sourceId, index, System.Text.Json.JsonSerializer.Serialize(variant), null, error, ct); }
        catch (Exception auditError) when (auditError is not OperationCanceledException || !ct.IsCancellationRequested)
        { logger.LogError(auditError, "Failed to audit derivative variant {VariantIndex} for source {FileId}", index, sourceId); }
    }

    private async Task<DerivativeVariantOutcome> CreateOne(FileAsset source, byte[] sourceBytes, DerivativeVariant variant, int index, string actor, CancellationToken ct)
    {
        ValidateVariant(variant);
        var focalX = variant.FocalX ?? 0.5;
        var focalY = variant.FocalY ?? 0.5;
        var processor = imageProcessor ?? throw new InvalidOperationException("Image derivative processor is not registered.");
        ProcessedDerivative output;
        try { output = processor.Process(sourceBytes, variant.Format, variant.Width, variant.Height, variant.Fit, focalX, focalY, variant.Background, variant.Quality); }
        catch (DerivativeFailure) { throw; }
        catch { throw new DerivativeFailure("decode_failed"); }
        if (output.Bytes.Length > FileUploadPolicy.MaxBytes) throw new DerivativeFailure("output_too_large");
        var sourceName = Path.GetFileNameWithoutExtension(source.OriginalFileName);
        var rawName = variant.Name is null ? sourceName : variant.Name;
        var basename = variant.Name is null ? TruncateGrapheme(rawName, 200 - (variant.Width + "x" + variant.Height).Length - 1) : rawName;
        if (basename.Length == 0) basename = "image";
        var candidate = $"{basename}-{variant.Width}x{variant.Height}.{output.Extension}";
        if (candidate.Length > 255) throw new DerivativeFailure("invalid_variant");
        var id = Guid.NewGuid(); var key = id.ToString("N");
        var spec = new DerivativeSpec(1, variant.Width, variant.Height, variant.Fit, variant.Fit == "cover" ? focalX : null,
            variant.Fit == "cover" ? focalY : null, variant.Background, variant.Format, variant.Quality);
        var warnings = output.Warnings;
        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var actual = suffix == 1 ? candidate : AddSuffix(candidate, suffix);
            if (actual.Length > 255) throw new DerivativeFailure("name_suffix_exhausted");
            var file = new FileAsset(id, actual, output.ContentType, output.Bytes.Length, key, actor, clock.GetUtcNow().UtcDateTime,
                output.Width, output.Height, source.Id, Preset(variant), System.Text.Json.JsonSerializer.Serialize(spec));
            await storage.Put(key, new MemoryStream(output.Bytes, writable: false), output.Bytes.Length, output.ContentType, ct);
            try
            {
                await files.ReserveDerivativeAndAdd(file, actual, ct);
                return new(index, ToMetadata(file, source.OriginalFileName), "/files/" + id.ToString("D"), null, warnings);
            }
            catch (DerivativeNameCollisionException)
            {
                await storage.Delete(key, CancellationToken.None);
                if (suffix == 1000) throw new DerivativeFailure("name_suffix_exhausted");
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { await storage.Delete(key, CancellationToken.None); }
                catch (Exception cleanupError) { logger.LogError(cleanupError, "Failed to clean up cancelled derivative object {FileId}", id); }
                throw;
            }
            catch
            {
                try { await storage.Delete(key, CancellationToken.None); }
                catch (Exception cleanupError) { logger.LogError(cleanupError, "Failed to clean up orphan derivative object {FileId}", id); }
                throw new DerivativeFailure("metadata_persist_failed");
            }
        }
        throw new DerivativeFailure("name_suffix_exhausted");
    }

    private static FileMetadata ToMetadata(FileAsset file, string? sourceName = null) => new(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Width, file.Height, file.UploadedBy, file.UploadedAtUtc,
        file.DerivedFromFileId is null ? null : new DerivedFromMetadata(file.DerivedFromFileId.Value, sourceName ?? file.DerivedFromFile?.OriginalFileName ?? string.Empty), file.DerivedPreset,
        file.DerivativeSpecJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<DerivativeSpec>(file.DerivativeSpecJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    private static void ValidateVariant(DerivativeVariant v)
    {
        if (v.Width is < 16 or > 4000 || v.Height is < 16 or > 4000) throw new DerivativeFailure("invalid_dimensions");
        if (v.Fit is not ("cover" or "contain") || v.Format is not ("keep" or "webp" or "jpg" or "png")
            || v.Quality is not (70 or 80 or 90) || v.Background != "transparent" && !System.Text.RegularExpressions.Regex.IsMatch(v.Background, "^#[0-9a-fA-F]{6}$"))
            throw new DerivativeFailure("invalid_variant");
        if ((v.Fit == "contain" && (v.FocalX.HasValue || v.FocalY.HasValue)) || v.FocalX is < 0 or > 1 || v.FocalY is < 0 or > 1)
            throw new DerivativeFailure("invalid_variant");
        if (v.Name is not null && (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 200 || v.Name.Any(char.IsControl) || v.Name.IndexOfAny(['/', '\\']) >= 0))
            throw new DerivativeFailure("invalid_variant");
    }
    private static string Preset(DerivativeVariant v) => (v.Width, v.Height) switch { (600, 600) => "home-600x600", (1000, 1000) => "product-detail-1000x1000", _ => "custom" };
    private static string AddSuffix(string fileName, int suffix) { var ext = Path.GetExtension(fileName); return fileName[..^ext.Length] + "-" + suffix + ext; }
    private static string TruncateGrapheme(string value, int max) { var e = System.Globalization.StringInfo.GetTextElementEnumerator(value); var result = new System.Text.StringBuilder(); while (e.MoveNext()) { var x = e.GetTextElement(); if (result.Length + x.Length > max) break; result.Append(x); } return result.ToString(); }
}

public sealed class DerivativeFailure(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class DerivativeNameCollisionException : Exception { }
public sealed class FileObjectTooLargeException : Exception { }
