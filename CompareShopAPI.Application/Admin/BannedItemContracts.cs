using CompareShopAPI.Domain.Admin;

namespace CompareShopAPI.Application.Admin;

public sealed record BannedItemDto(int Id, string TargetType, string? BrandName, int? ProductId, string? ProductName,
    string Reason, bool IsActive, string CreatedBy, DateTime CreatedAtUtc, string? RevokedBy, DateTime? RevokedAtUtc);
public sealed record BannedItemInput(string TargetType, string? BrandName, int? ProductId, string Reason);
public sealed record BannedItemListFilter(string? TargetType, bool Active, string? Search, int Page, int PageSize);
public sealed record BannedItemEvent(Guid EventId, string EventType, int BannedItemId, string TargetType,
    string? BrandName, int? ProductId, string Reason, string Actor, DateTime OccurredAtUtc);

public interface IBannedItemData
{
    Task<Page<BannedItemDto>> List(BannedItemListFilter filter, CancellationToken ct);
    Task<BannedItemDto?> Get(int id, CancellationToken ct);
    Task<bool> ProductExists(int id, CancellationToken ct);
    Task<BannedItemDto> Create(BannedItem item, CancellationToken ct);
    Task<bool> Revoke(int id, string actor, DateTime nowUtc, CancellationToken ct);
}

public sealed class BannedItemNotFoundException() : Exception("The requested product or banned item was not found.");
public sealed class BannedItemConflictException() : Exception("An active ban already exists for this target.");

public sealed class BannedItemService(IBannedItemData data, IAdminPermissionReader permissions, TimeProvider clock)
{
    public async Task<Page<BannedItemDto>> List(string? subject, string? targetType, bool? active, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.ComplianceRead, ct);
        ValidatePage(page, pageSize);
        if (targetType is not null && !TryParseTargetType(targetType, out _))
            throw new ArgumentException("targetType must be Brand or Product.", nameof(targetType));
        if (search?.Length > 200) throw new ArgumentException("search cannot exceed 200 characters.", nameof(search));
        return await data.List(new(targetType, active ?? true, search?.Trim(), page, pageSize), ct);
    }

    public async Task<BannedItemDto?> Get(string? subject, int id, CancellationToken ct)
    {
        await Authorize(subject, AdminPermissions.ComplianceRead, ct);
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return await data.Get(id, ct);
    }

    public async Task<BannedItemDto> Create(string? subject, BannedItemInput input, CancellationToken ct)
    {
        var actor = await Authorize(subject, AdminPermissions.ComplianceManage, ct);
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (!TryParseTargetType(input.TargetType, out var targetType))
            throw new ArgumentException("targetType must be Brand or Product.", nameof(input));

        var item = targetType switch
        {
            BannedTargetType.Brand when input.ProductId is null => BannedItem.ForBrand(input.BrandName ?? string.Empty, input.Reason, actor, clock.GetUtcNow().UtcDateTime),
            BannedTargetType.Product when input.BrandName is null && input.ProductId is > 0 => BannedItem.ForProduct(input.ProductId.Value, input.Reason, actor, clock.GetUtcNow().UtcDateTime),
            BannedTargetType.Product when input.ProductId is > 0 && !string.IsNullOrWhiteSpace(input.BrandName) => throw new ArgumentException("Product bans cannot include brandName.", nameof(input)),
            BannedTargetType.Brand when input.ProductId is not null => throw new ArgumentException("Brand bans cannot include productId.", nameof(input)),
            BannedTargetType.Product when input.ProductId is null => throw new ArgumentException("productId is required for product bans.", nameof(input)),
            _ => throw new ArgumentException("A positive productId is required for product bans.", nameof(input))
        };

        if (targetType == BannedTargetType.Product && !await data.ProductExists(item.ProductId!.Value, ct))
            throw new BannedItemNotFoundException();
        return await data.Create(item, ct);
    }

    public async Task<bool> Revoke(string? subject, int id, CancellationToken ct)
    {
        var actor = await Authorize(subject, AdminPermissions.ComplianceManage, ct);
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return await data.Revoke(id, actor, clock.GetUtcNow().UtcDateTime, ct);
    }

    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        if (!await permissions.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }

    private static bool TryParseTargetType(string? value, out BannedTargetType targetType)
    {
        if (Enum.TryParse(value, true, out targetType) && Enum.IsDefined(targetType))
            return string.Equals(value?.Trim(), targetType.ToString(), StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "Page must be at least 1.");
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
    }
}
