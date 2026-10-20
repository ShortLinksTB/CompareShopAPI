namespace CompareShopAPI.Domain.Admin;

using CompareShopAPI.Domain.Catalog;

public enum BannedTargetType
{
    Brand,
    Product
}

public sealed class BannedItem
{
    private BannedItem() { }

    private BannedItem(BannedTargetType targetType, string? brandName, string? normalizedBrandName, int? productId,
        string reason, string createdBy, DateTime createdAtUtc)
    {
        TargetType = targetType;
        BrandName = brandName;
        NormalizedBrandName = normalizedBrandName;
        ProductId = productId;
        Reason = reason;
        CreatedBy = createdBy;
        CreatedAtUtc = RequireUtc(createdAtUtc, nameof(createdAtUtc));
        IsActive = true;
    }

    public int Id { get; private set; }
    public BannedTargetType TargetType { get; private set; }
    public string? BrandName { get; private set; }
    public string? NormalizedBrandName { get; private set; }
    public int? ProductId { get; private set; }
    public Product? Product { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public string? RevokedBy { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public static BannedItem ForBrand(string brandName, string reason, string actor, DateTime nowUtc)
    {
        var name = RequiredText(brandName, 200, nameof(brandName));
        return new(BannedTargetType.Brand, name, NormalizeBrandName(name), null,
            RequiredText(reason, 1000, nameof(reason)), RequiredText(actor, 200, nameof(actor)), nowUtc);
    }

    public static BannedItem ForProduct(int productId, string reason, string actor, DateTime nowUtc)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        return new(BannedTargetType.Product, null, null, productId,
            RequiredText(reason, 1000, nameof(reason)), RequiredText(actor, 200, nameof(actor)), nowUtc);
    }

    public bool Revoke(string actor, DateTime nowUtc)
    {
        if (!IsActive) return false;
        RevokedBy = RequiredText(actor, 200, nameof(actor));
        RevokedAtUtc = RequireUtc(nowUtc, nameof(nowUtc));
        IsActive = false;
        return true;
    }

    public static string NormalizeBrandName(string value) => value.Trim().ToUpperInvariant();

    private static string RequiredText(string? value, int maxLength, string parameter)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ArgumentException($"A non-empty value up to {maxLength} characters is required.", parameter);
        return normalized;
    }

    private static DateTime RequireUtc(DateTime value, string parameter)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamps must be UTC.", parameter);
        return value;
    }
}

public sealed class BannedItemOutboxMessage
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    public void ScheduleRetry(DateTime nowUtc, string errorCode)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamps must be UTC.", nameof(nowUtc));
        AttemptCount++;
        var delaySeconds = Math.Min(300, 1 << Math.Min(8, AttemptCount - 1));
        NextAttemptAtUtc = nowUtc.AddSeconds(delaySeconds);
        LastError = string.IsNullOrWhiteSpace(errorCode) ? "publish_failed" : errorCode[..Math.Min(errorCode.Length, 80)];
    }
}
