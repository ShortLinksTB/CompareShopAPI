using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace CompareShopAPI.Infrastructure.Catalog;
public sealed class EfCatalogQueries(CompareShopDbContext db, TimeProvider clock) : ICatalogQueries
{
    private IQueryable<CompareShopAPI.Domain.Catalog.ProductOffer> VisibleOffers() => VisibleCatalogOffers.Query(db, clock);
    private IQueryable<CompareShopAPI.Domain.Catalog.Product> ActiveProducts() => BannedItemVisibility.ExcludeBannedProducts(db,
        db.Products.AsNoTracking().Where(p => p.IsActive && p.Category.IsActive));
    public async Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new CategoryDto(c.Id, c.Slug, c.Name)).ToListAsync(ct);
    public async Task<PageDto<ProductListDto>> Products(ProductFilter f, CancellationToken ct)
    {
        var products = ActiveProducts();
        if (!string.IsNullOrWhiteSpace(f.Search)) products = products.Where(p => p.Name.Contains(f.Search));
        if (f.CategoryId.HasValue) products = products.Where(p => p.CategoryId == f.CategoryId);
        if (f.ShopId.HasValue || f.MinPrice.HasValue || f.MaxPrice.HasValue)
        {
            var matching = VisibleOffers();
            if (f.ShopId.HasValue) matching = matching.Where(o => o.PartnerShopId == f.ShopId);
            if (f.MinPrice.HasValue) matching = matching.Where(o => o.PriceVnd >= f.MinPrice);
            if (f.MaxPrice.HasValue) matching = matching.Where(o => o.PriceVnd <= f.MaxPrice);
            products = products.Where(p => matching.Any(o => o.ProductId == p.Id));
        }
        var total = await products.CountAsync(ct);
        var skip = (long)(f.Page - 1) * f.PageSize;
        if (skip >= total) return new PageDto<ProductListDto>([], f.Page, f.PageSize, total, (int)Math.Ceiling(total / (double)f.PageSize));
        var rows = await products.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((int)skip).Take(f.PageSize)
            .Select(p => new { p.Id, p.Slug, p.Name, p.Brand, Category = new CategoryDto(p.Category.Id, p.Category.Slug, p.Category.Name),
                Summary = new OfferSummaryDto(VisibleOffers().Where(o => o.ProductId == p.Id).Select(o => (int?)o.PriceVnd).Min(), VisibleOffers().Where(o => o.ProductId == p.Id).Select(o => (int?)o.PriceVnd).Max(), VisibleOffers().Count(o => o.ProductId == p.Id)) }).ToListAsync(ct);
        var productIds = rows.Select(p => p.Id).ToArray();
        var representatives = await VisibleOffers().Where(o => productIds.Contains(o.ProductId)).OrderBy(o => o.PriceVnd).ThenBy(o => o.PartnerShopId).ThenBy(o => o.Id)
            .Select(o => new { o.ProductId, o.ImageUrl, o.SpecificationsJson }).ToListAsync(ct);
        var representativeByProduct = representatives.GroupBy(o => o.ProductId).ToDictionary(group => group.Key, group => group.First());
        var items = rows.Select(p =>
        {
            representativeByProduct.TryGetValue(p.Id, out var offer);
            return new ProductListDto(p.Id, p.Slug, p.Name, p.Brand, p.Category, p.Summary, offer?.ImageUrl, ParseSpecs(offer?.SpecificationsJson));
        }).ToList();
        return new PageDto<ProductListDto>(items, f.Page, f.PageSize, total, (int)Math.Ceiling(total / (double)f.PageSize));
    }
    public async Task<ProductDetailDto?> Product(string slug, CancellationToken ct)
    {
        var row = await ActiveProducts().Where(x => x.Slug == slug)
            .Select(x => new { x.Id, x.Slug, x.Name, x.Brand, x.Description, Category = new CategoryDto(x.Category.Id, x.Category.Slug, x.Category.Name),
                Summary = new OfferSummaryDto(VisibleOffers().Where(o => o.ProductId == x.Id).Select(o => (int?)o.PriceVnd).Min(), VisibleOffers().Where(o => o.ProductId == x.Id).Select(o => (int?)o.PriceVnd).Max(), VisibleOffers().Count(o => o.ProductId == x.Id)),
                Groups = x.SpecificationGroups.OrderBy(g => g.SortOrder).Select(g => new SpecificationGroupDto(g.Name, g.Items.OrderBy(i => i.SortOrder).Select(i => new SpecificationItemDto(i.Name, i.Value)).ToList())).ToList() }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var representative = (await Offers(row.Id, ct))?.FirstOrDefault();
        return new ProductDetailDto(row.Id, row.Slug, row.Name, row.Brand, row.Description, row.Category, row.Summary, row.Groups, representative?.ImageUrl, representative?.Specifications);
    }
    public async Task<IReadOnlyList<OfferDto>?> Offers(int productId, CancellationToken ct)
    {
        if (!await ActiveProducts().AnyAsync(p => p.Id == productId, ct)) return null;
        var rows = await VisibleOffers().Where(o => o.ProductId == productId).OrderBy(o => o.PriceVnd).ThenBy(o => o.PartnerShopId).ThenBy(o => o.Id).Select(o => new { o.Id, o.PriceVnd, o.Url, o.UpdatedAtUtc, o.PartnerShopId, o.PartnerShop.Slug, o.PartnerShop.Name, o.OriginalPriceVnd, o.ImageUrl, o.StockStatus, o.SpecificationsJson }).ToListAsync(ct);
        return rows.Select(o => new OfferDto(o.Id, o.PriceVnd, o.Url, o.UpdatedAtUtc, o.PartnerShopId, o.Slug, o.Name, o.OriginalPriceVnd, o.ImageUrl, o.StockStatus, ParseSpecs(o.SpecificationsJson))).ToList();
    }
    public async Task<ShopDto?> Shop(string slug, CancellationToken ct) => await db.PartnerShops.AsNoTracking().Where(s => s.IsActive && s.Slug == slug).Select(s => new ShopDto(s.Id, s.Slug, s.Name, s.WebsiteUrl)).FirstOrDefaultAsync(ct);
    public async Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(string query, int limit, CancellationToken ct) =>
        await ActiveProducts().Where(p => p.Name.Contains(query))
            .OrderByDescending(p => p.Name.StartsWith(query)).ThenBy(p => p.Name).ThenBy(p => p.Id)
            .Take(limit).Select(p => new ProductSuggestionDto(p.Id, p.Slug, p.Name)).ToListAsync(ct);

    private static IReadOnlyDictionary<string,string>? ParseSpecs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
