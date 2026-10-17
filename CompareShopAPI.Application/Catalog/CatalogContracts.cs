using CompareShopAPI.Domain.Catalog;
using System.Globalization;

namespace CompareShopAPI.Application.Catalog;

public sealed record CategoryDto(int Id, string Slug, string Name);
public sealed record PageDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages);
public sealed record OfferSummaryDto(int? MinPrice, int? MaxPrice, int OfferCount);
public sealed record OfferDto(int Id, int Price, string? Url, DateTime UpdatedAt, int ShopId, string ShopSlug, string ShopName);
public sealed record SpecificationItemDto(string Name, string Value);
public sealed record SpecificationGroupDto(string Name, IReadOnlyList<SpecificationItemDto> Items);
public sealed record ProductListDto(int Id, string Slug, string Name, string? Brand, CategoryDto Category, OfferSummaryDto OfferSummary);
public sealed record ProductDetailDto(int Id, string Slug, string Name, string? Brand, string? Description, CategoryDto Category, OfferSummaryDto OfferSummary, IReadOnlyList<SpecificationGroupDto> SpecificationGroups);
public sealed record ShopDto(int Id, string Slug, string Name, string? WebsiteUrl);
public sealed record ProductSuggestionDto(int Id, string Slug, string Name);
public sealed record PriceHistoryDto(DateTime Date, int MinPrice, int? ShopId);
public sealed record ProductFilter(int Page = 1, int PageSize = 20, int? CategoryId = null, int? MinPrice = null, int? MaxPrice = null, int? ShopId = null, string? Search = null);

public interface ICatalogQueries
{
    Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct);
    Task<PageDto<ProductListDto>> Products(ProductFilter filter, CancellationToken ct);
    Task<ProductDetailDto?> Product(string slug, CancellationToken ct);
    Task<IReadOnlyList<OfferDto>?> Offers(int productId, CancellationToken ct);
    Task<ShopDto?> Shop(string slug, CancellationToken ct);
    Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(string query, int limit, CancellationToken ct);
}

public interface IPriceHistoryStore
{
    Task<IReadOnlyList<PriceHistoryDto>?> History(int productId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<PublicPriceMinimum?> CurrentMinimum(int productId, CancellationToken ct);
    Task<int?> LatestMinimum(int productId, CancellationToken ct);
    Task Append(int productId, DateTime dateUtc, PublicPriceMinimum minimum, CancellationToken ct);
}

public sealed class CatalogService(ICatalogQueries queries)
{
    public Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => queries.Categories(ct);
    public Task<PageDto<ProductListDto>> Products(ProductFilter filter, CancellationToken ct)
    {
        if (filter.Page < 1 || filter.PageSize < 1 || filter.PageSize > 100 || filter.MinPrice < 0 || filter.MaxPrice < 0 || filter.MinPrice > filter.MaxPrice)
            throw new ArgumentOutOfRangeException(nameof(filter), "Invalid page or price range.");
        var search = filter.Search?.Trim();
        if (!string.IsNullOrEmpty(search) && search.Length is < 2 or > 80)
            throw new ArgumentOutOfRangeException(nameof(filter), "Search must contain between 2 and 80 characters.");
        return queries.Products(filter with { Search = string.IsNullOrEmpty(search) ? null : search }, ct);
    }
    public Task<ProductDetailDto?> Product(string slug, CancellationToken ct) => queries.Product(slug, ct);
    public Task<IReadOnlyList<OfferDto>?> Offers(int productId, CancellationToken ct) => queries.Offers(productId, ct);
    public Task<ShopDto?> Shop(string slug, CancellationToken ct) => queries.Shop(slug, ct);

    public async Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(string? query, int limit, CancellationToken ct)
    {
        var normalized = query?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length is < 2 or > 80)
            throw new ArgumentException("Query must contain between 2 and 80 characters.", nameof(query));
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10.");
        return await queries.Suggestions(normalized, limit, ct);
    }
}

public sealed class PriceHistoryService(IPriceHistoryStore store, TimeProvider clock)
{
    public async Task<IReadOnlyList<PriceHistoryDto>?> History(int productId, string? from, string? to, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        var fromUtcValue = ParseUtc(from, nameof(from));
        var toUtcValue = ParseUtc(to, nameof(to));

        var now = clock.GetUtcNow().UtcDateTime;
        var toUtc = toUtcValue?.UtcDateTime ?? now;
        var fromUtc = fromUtcValue?.UtcDateTime ?? toUtc.AddDays(-90);
        if (fromUtc > toUtc || toUtc - fromUtc > TimeSpan.FromDays(365))
            throw new ArgumentException("Date range must be ordered and no longer than 365 days.", nameof(from));
        return await store.History(productId, fromUtc, toUtc, ct);
    }

    private static DateTimeOffset? ParseUtc(string? value, string parameterName)
    {
        if (value is null) return null;
        var text = value.Trim();
        var hasUtcDesignator = text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasUtcDesignator || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero)
            throw new ArgumentException("Dates must be ISO 8601 UTC timestamps ending in Z or +00:00.", parameterName);
        return parsed;
    }

    public async Task RecordMinimum(int productId, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        var minimum = await store.CurrentMinimum(productId, ct);
        if (minimum is null || await store.LatestMinimum(productId, ct) == minimum.MinPriceVnd) return;
        await store.Append(productId, clock.GetUtcNow().UtcDateTime, minimum, ct);
    }
}
