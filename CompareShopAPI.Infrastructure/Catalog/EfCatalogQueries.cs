using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace CompareShopAPI.Infrastructure.Catalog;
public sealed class EfCatalogQueries(CompareShopDbContext db, TimeProvider clock) : ICatalogQueries
{
    private IQueryable<CompareShopAPI.Domain.Catalog.ProductOffer> VisibleOffers(int countryId) =>
        BannedItemVisibility.ExcludeBannedProducts(db, VisibleCatalogOffers.Query(db, clock)
            .Where(o => o.PartnerShop.CountryId == countryId && o.PartnerShop.IsActive), countryId);
    private IQueryable<CompareShopAPI.Domain.Catalog.Product> ActiveProducts(int countryId) => BannedItemVisibility.ExcludeBannedProducts(db,
        db.Products.AsNoTracking().Where(p => p.IsActive && p.Category.IsActive && p.Category.CountryId == countryId
            && VisibleOffers(countryId).Any(o => o.ProductId == p.Id)), countryId);
    public async Task<IReadOnlyList<CategoryDto>> Categories(int countryId, int? parentId, CancellationToken ct)
    {
        var categories = db.Categories.AsNoTracking().Where(category => category.IsActive && category.CountryId == countryId);
        categories = parentId.HasValue
            ? categories.Where(category => category.Level == 2 && category.ParentCategoryId == parentId.Value &&
                db.Categories.Any(parent => parent.Id == parentId.Value && parent.IsActive && parent.Level == 1 && parent.CountryId == countryId))
            : categories.Where(category => category.Level == 1);
        return await categories.OrderBy(category => category.SortOrder).ThenBy(category => category.Name).ThenBy(category => category.Id)
            .Select(category => new CategoryDto(category.Id, category.Slug, category.Name, category.ParentCategoryId, category.Level))
            .ToListAsync(ct);
    }
    public async Task<PageDto<ProductListDto>> Products(ProductFilter f, CancellationToken ct)
    {
        var products = ActiveProducts(f.CountryId);
        if (!string.IsNullOrWhiteSpace(f.Search)) products = products.Where(p => p.Name.Contains(f.Search));
        if (f.CategoryId.HasValue) products = products.Where(p => p.CategoryId == f.CategoryId);
        if (f.ShopId.HasValue || f.MinPrice.HasValue || f.MaxPrice.HasValue)
        {
            var matching = VisibleOffers(f.CountryId);
            if (f.ShopId.HasValue) matching = matching.Where(o => o.PartnerShopId == f.ShopId);
            if (f.MinPrice.HasValue) matching = matching.Where(o => o.PriceVnd >= f.MinPrice);
            if (f.MaxPrice.HasValue) matching = matching.Where(o => o.PriceVnd <= f.MaxPrice);
            products = products.Where(p => matching.Any(o => o.ProductId == p.Id));
        }
        var total = await products.CountAsync(ct);
        var skip = (long)(f.Page - 1) * f.PageSize;
        if (skip >= total) return new PageDto<ProductListDto>([], f.Page, f.PageSize, total, (int)Math.Ceiling(total / (double)f.PageSize));
        var rows = await products.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((int)skip).Take(f.PageSize)
            .Select(p => new { p.Id, p.Slug, p.Name, p.Brand, p.ImageUrl, Category = new CategoryDto(p.Category.Id, p.Category.Slug, p.Category.Name, p.Category.ParentCategoryId, p.Category.Level),
                Summary = new OfferSummaryDto(VisibleOffers(f.CountryId).Where(o => o.ProductId == p.Id).Select(o => (int?)o.PriceVnd).Min(), VisibleOffers(f.CountryId).Where(o => o.ProductId == p.Id).Select(o => (int?)o.PriceVnd).Max(), VisibleOffers(f.CountryId).Count(o => o.ProductId == p.Id)) }).ToListAsync(ct);
        var productIds = rows.Select(p => p.Id).ToArray();
        var representatives = await VisibleOffers(f.CountryId).Where(o => productIds.Contains(o.ProductId)).OrderBy(o => o.PriceVnd).ThenBy(o => o.PartnerShopId).ThenBy(o => o.Id)
            .Select(o => new { o.ProductId, o.ImageUrl, o.SpecificationsJson }).ToListAsync(ct);
        var representativeByProduct = representatives.GroupBy(o => o.ProductId).ToDictionary(group => group.Key, group => group.First());
        var mainImages = await db.ProductImages.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).OrderBy(x => x.SortOrder)
            .Select(x => new { x.ProductId, x.FileId, x.ExternalUrl, x.Card600FileId }).ToListAsync(ct);
        var mainImageByProduct = mainImages.GroupBy(x => x.ProductId).ToDictionary(group => group.Key, group => group.First());
        var items = rows.Select(p =>
        {
            representativeByProduct.TryGetValue(p.Id, out var offer);
            var imageUrl = mainImageByProduct.TryGetValue(p.Id, out var main)
                ? main.Card600FileId.HasValue ? $"/files/{main.Card600FileId.Value:D}" : main.ExternalUrl ?? (main.FileId.HasValue ? $"/files/{main.FileId.Value:D}" : null)
                : offer?.ImageUrl ?? p.ImageUrl;
            return new ProductListDto(p.Id, p.Slug, p.Name, p.Brand, p.Category, p.Summary, imageUrl, ParseSpecs(offer?.SpecificationsJson));
        }).ToList();
        return new PageDto<ProductListDto>(items, f.Page, f.PageSize, total, (int)Math.Ceiling(total / (double)f.PageSize));
    }
    public async Task<ProductDetailDto?> Product(int countryId, string slug, CancellationToken ct)
    {
        var row = await ActiveProducts(countryId).Where(x => x.Slug == slug)
            .Select(x => new { x.Id, x.Slug, x.Name, x.Brand, x.Description, x.ImageUrl, Category = new CategoryDto(x.Category.Id, x.Category.Slug, x.Category.Name, x.Category.ParentCategoryId, x.Category.Level),
                Summary = new OfferSummaryDto(VisibleOffers(countryId).Where(o => o.ProductId == x.Id).Select(o => (int?)o.PriceVnd).Min(), VisibleOffers(countryId).Where(o => o.ProductId == x.Id).Select(o => (int?)o.PriceVnd).Max(), VisibleOffers(countryId).Count(o => o.ProductId == x.Id)),
                Groups = x.SpecificationGroups.OrderBy(g => g.SortOrder).Select(g => new SpecificationGroupDto(g.Name, g.Items.OrderBy(i => i.SortOrder).Select(i => new SpecificationItemDto(i.Name, i.Value)).ToList())).ToList() }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var representative = (await Offers(countryId, row.Id, ct))?.FirstOrDefault();
        var images = await db.ProductImages.AsNoTracking().Where(x => x.ProductId == row.Id).OrderBy(x => x.SortOrder)
            .Select(x => new { x.FileId, x.ExternalUrl, x.Card600FileId, x.Detail1000FileId, x.AltText }).ToListAsync(ct);
        var imageDtos = images.Select(x =>
        {
            var source = x.ExternalUrl ?? (x.FileId.HasValue ? $"/files/{x.FileId.Value:D}" : string.Empty);
            return new ProductImageDto(x.Detail1000FileId.HasValue ? $"/files/{x.Detail1000FileId.Value:D}" : source,
                x.Card600FileId.HasValue ? $"/files/{x.Card600FileId.Value:D}" : source, x.AltText);
        }).ToArray();
        var imageUrl = imageDtos.Length > 0 ? imageDtos[0].Url600 : representative?.ImageUrl ?? row.ImageUrl;
        return new ProductDetailDto(row.Id, row.Slug, row.Name, row.Brand, row.Description, row.Category, row.Summary, row.Groups,
            imageUrl, representative?.Specifications, imageDtos);
    }
    public async Task<IReadOnlyList<OfferDto>?> Offers(int countryId, int productId, CancellationToken ct)
    {
        if (!await ActiveProducts(countryId).AnyAsync(p => p.Id == productId, ct)) return null;
        var rows = await VisibleOffers(countryId).Where(o => o.ProductId == productId).OrderBy(o => o.PriceVnd).ThenBy(o => o.PartnerShopId).ThenBy(o => o.Id).Select(o => new { o.Id, o.PriceVnd, o.Url, o.UpdatedAtUtc, o.PartnerShopId, o.PartnerShop.Slug, o.PartnerShop.Name, o.OriginalPriceVnd, o.ImageUrl, o.StockStatus, o.SpecificationsJson }).ToListAsync(ct);
        return rows.Select(o => new OfferDto(o.Id, o.PriceVnd, o.Url, o.UpdatedAtUtc, o.PartnerShopId, o.Slug, o.Name, o.OriginalPriceVnd, o.ImageUrl, o.StockStatus, ParseSpecs(o.SpecificationsJson))).ToList();
    }
    public async Task<ShopDto?> Shop(int countryId, string slug, CancellationToken ct) => await db.PartnerShops.AsNoTracking().Where(s => s.IsActive && s.CountryId == countryId && s.Slug == slug).Select(s => new ShopDto(s.Id, s.Slug, s.Name, s.WebsiteUrl)).FirstOrDefaultAsync(ct);
    public async Task<IReadOnlyList<HomePartnerDto>> HomePartners(int countryId, CancellationToken ct) => await db.PartnerShops.AsNoTracking()
        .Where(s => s.IsActive && s.CountryId == countryId && s.ShowOnHome)
        .OrderBy(s => s.HomeDisplayOrder.HasValue ? 0 : 1).ThenBy(s => s.HomeDisplayOrder).ThenBy(s => s.Id)
        .Select(s => new HomePartnerDto(s.Name, s.Slug, s.WebsiteUrl)).ToListAsync(ct);
    public async Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(int countryId, string query, int limit, CancellationToken ct) =>
        await ActiveProducts(countryId).Where(p => p.Name.Contains(query))
            .OrderByDescending(p => p.Name.StartsWith(query)).ThenBy(p => p.Name).ThenBy(p => p.Id)
            .Take(limit).Select(p => new ProductSuggestionDto(p.Id, p.Slug, p.Name)).ToListAsync(ct);

    private static IReadOnlyDictionary<string,string>? ParseSpecs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
