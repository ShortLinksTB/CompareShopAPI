using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Catalog;

internal static class VisibleCatalogOffers
{
    public static IQueryable<ProductOffer> Query(CompareShopDbContext db, TimeProvider clock)
    {
        var freshnessCutoff = clock.GetUtcNow().UtcDateTime.AddDays(-CatalogOfferPolicy.FreshnessDays);
        return BannedItemVisibility.ExcludeBannedProducts(db, db.ProductOffers.AsNoTracking())
            .Where(o => o.IsActive && !o.IsOutlier && o.UpdatedAtUtc >= freshnessCutoff && o.Product.IsActive && o.PartnerShop.IsActive);
    }
}

public sealed class EfPriceHistoryStore(CompareShopDbContext db, TimeProvider clock) : IPriceHistoryStore
{
    private IQueryable<Product> ActiveProducts() => BannedItemVisibility.ExcludeBannedProducts(db,
        db.Products.AsNoTracking().Where(p => p.IsActive && p.Category.IsActive));

    public async Task<IReadOnlyList<PriceHistoryDto>?> History(int productId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (!await ActiveProducts().AnyAsync(p => p.Id == productId, ct)) return null;
        var rows = await db.PriceHistoryPoints.AsNoTracking()
            .Where(point => point.ProductId == productId && point.DateUtc >= fromUtc && point.DateUtc <= toUtc)
            .OrderBy(point => point.DateUtc).ThenBy(point => point.Id)
            .Select(point => new { point.DateUtc, point.MinPriceVnd, point.PartnerShopId }).ToListAsync(ct);
        return rows.Select(point => new PriceHistoryDto(DateTime.SpecifyKind(point.DateUtc, DateTimeKind.Utc), point.MinPriceVnd, point.PartnerShopId)).ToList();
    }

    public async Task<PublicPriceMinimum?> CurrentMinimum(int productId, CancellationToken ct) =>
        await VisibleCatalogOffers.Query(db, clock).Where(offer => offer.ProductId == productId)
            .OrderBy(offer => offer.PriceVnd).ThenBy(offer => offer.PartnerShopId).ThenBy(offer => offer.Id)
            .Select(offer => new PublicPriceMinimum(offer.PriceVnd, offer.PartnerShopId)).FirstOrDefaultAsync(ct);

    public async Task<int?> LatestMinimum(int productId, CancellationToken ct) =>
        await db.PriceHistoryPoints.AsNoTracking().Where(point => point.ProductId == productId)
            .OrderByDescending(point => point.DateUtc).ThenByDescending(point => point.Id)
            .Select(point => (int?)point.MinPriceVnd).FirstOrDefaultAsync(ct);

    public async Task Append(int productId, DateTime dateUtc, PublicPriceMinimum minimum, CancellationToken ct)
    {
        db.PriceHistoryPoints.Add(PriceHistoryPoint.Create(productId, dateUtc, minimum.MinPriceVnd, minimum.PartnerShopId));
        await db.SaveChangesAsync(ct);
    }
}
