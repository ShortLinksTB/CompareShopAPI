namespace CompareShopAPI.Domain.Files;

public enum FileLifecycleState { Active = 0, Deleting = 1, Deleted = 2 }

public sealed class FileAsset
{
    private FileAsset() { }

    public FileAsset(Guid id, string originalFileName, string contentType, long sizeBytes, string objectKey,
        string uploadedBy, DateTime uploadedAtUtc, int? width = null, int? height = null,
        Guid? derivedFromFileId = null, string? derivedPreset = null, string? derivativeSpecJson = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("File ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255
            || originalFileName.IndexOfAny(['/', '\\']) >= 0 || originalFileName.Any(char.IsControl))
            throw new ArgumentException("A valid original filename is required.", nameof(originalFileName));
        if (!FileUploadPolicy.AllowedContentTypes.Contains(contentType)) throw new ArgumentException("Unsupported file type.", nameof(contentType));
        if (sizeBytes is <= 0 or > FileUploadPolicy.MaxBytes) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Length > 512) throw new ArgumentException("Object key is required.", nameof(objectKey));
        if (string.IsNullOrWhiteSpace(uploadedBy) || uploadedBy.Length > 200) throw new ArgumentException("Uploader is required.", nameof(uploadedBy));
        if (width is <= 0 || height is <= 0 || width.HasValue != height.HasValue) throw new ArgumentException("Image dimensions must be positive and supplied together.");
        Id = id;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        ObjectKey = objectKey;
        UploadedBy = uploadedBy;
        if (uploadedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Upload time must be UTC.", nameof(uploadedAtUtc));
        UploadedAtUtc = uploadedAtUtc;
        Width = width;
        Height = height;
        DerivedFromFileId = derivedFromFileId;
        DerivedPreset = derivedPreset;
        DerivativeSpecJson = derivativeSpecJson;
    }

    public Guid Id { get; private set; }
    public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public string UploadedBy { get; private set; } = string.Empty;
    public DateTime UploadedAtUtc { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;
    public Guid? DerivedFromFileId { get; private set; }
    public FileLifecycleState LifecycleState { get; private set; } = FileLifecycleState.Active;
    public string? DerivedPreset { get; private set; }
    public string? DerivativeSpecJson { get; private set; }
    public FileAsset? DerivedFromFile { get; private set; }

    public void BeginDeletion()
    {
        if (LifecycleState == FileLifecycleState.Deleted) return;
        LifecycleState = FileLifecycleState.Deleting;
    }

    public void CompleteDeletion() => LifecycleState = FileLifecycleState.Deleted;
}

public static class FileUploadPolicy
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/svg+xml", "application/pdf"
    };
}
