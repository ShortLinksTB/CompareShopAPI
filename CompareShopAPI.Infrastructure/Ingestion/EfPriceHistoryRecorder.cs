using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Ingestion;

public sealed class EfPriceHistoryRecorder(CompareShopDbContext db, TimeProvider clock) : IPriceHistoryRecorder
{
    public async Task RecordForChangedMinimums(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-CatalogOfferPolicy.FreshnessDays);
        var minima = await db.ProductOffers.AsNoTracking().Where(o => o.IsActive && !o.IsOutlier && o.UpdatedAtUtc >= cutoff && o.Product.IsActive && o.PartnerShop.IsActive)
            .GroupBy(o => o.ProductId).Select(g => new { ProductId = g.Key, Min = g.OrderBy(o => o.PriceVnd).ThenBy(o => o.PartnerShopId).Select(o => new { o.PriceVnd, o.PartnerShopId }).First() }).ToListAsync(ct);
        foreach (var item in minima)
        {
            var latest = await db.PriceHistoryPoints.Where(p => p.ProductId == item.ProductId).OrderByDescending(p => p.DateUtc).ThenByDescending(p => p.Id).Select(p => (int?)p.MinPriceVnd).FirstOrDefaultAsync(ct);
            if (latest != item.Min.PriceVnd) db.PriceHistoryPoints.Add(PriceHistoryPoint.Create(item.ProductId, clock.GetUtcNow().UtcDateTime, item.Min.PriceVnd, item.Min.PartnerShopId));
        }
        await db.SaveChangesAsync(ct);
    }
}
