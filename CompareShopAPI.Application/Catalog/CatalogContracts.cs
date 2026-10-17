using CompareShopAPI.Domain.Catalog;

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
public sealed record ProductFilter(int Page = 1, int PageSize = 20, int? CategoryId = null, int? MinPrice = null, int? MaxPrice = null, int? ShopId = null);

public interface ICatalogQueries
{
    Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct);
    Task<PageDto<ProductListDto>> Products(ProductFilter filter, CancellationToken ct);
    Task<ProductDetailDto?> Product(string slug, CancellationToken ct);
    Task<IReadOnlyList<OfferDto>?> Offers(int productId, CancellationToken ct);
    Task<ShopDto?> Shop(string slug, CancellationToken ct);
}

public sealed class CatalogService(ICatalogQueries queries)
{
    public Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => queries.Categories(ct);
    public Task<PageDto<ProductListDto>> Products(ProductFilter filter, CancellationToken ct)
    {
        if (filter.Page < 1 || filter.PageSize < 1 || filter.PageSize > 100 || filter.MinPrice < 0 || filter.MaxPrice < 0 || filter.MinPrice > filter.MaxPrice)
            throw new ArgumentOutOfRangeException(nameof(filter), "Invalid page or price range.");
        return queries.Products(filter, ct);
    }
    public Task<ProductDetailDto?> Product(string slug, CancellationToken ct) => queries.Product(slug, ct);
    public Task<IReadOnlyList<OfferDto>?> Offers(int productId, CancellationToken ct) => queries.Offers(productId, ct);
    public Task<ShopDto?> Shop(string slug, CancellationToken ct) => queries.Shop(slug, ct);
}
