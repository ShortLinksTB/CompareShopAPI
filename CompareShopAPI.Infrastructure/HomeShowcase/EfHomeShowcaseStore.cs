using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Application.HomeShowcase;
using CompareShopAPI.Domain.HomeShowcase;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.HomeShowcase;

public sealed class EfHomeShowcaseStore(CompareShopDbContext db, ICatalogQueries catalog, TimeProvider clock) : IHomeShowcaseStore
{
    public async Task<IReadOnlyList<HomeShowcaseSlot>> Slots(HomeShowcaseSnapshot snapshot, CancellationToken ct) =>
        await db.HomeShowcaseSlots.AsNoTracking().Where(slot => slot.Snapshot == snapshot).OrderBy(slot => slot.Section).ThenBy(slot => slot.SlotIndex).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<int, HomeShowcaseProductData>> Products(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<int, HomeShowcaseProductData>();
        var visibleIds = (await BannedItemVisibility.ExcludeBannedProducts(db, db.Products.AsNoTracking().Where(product => ids.Contains(product.Id)))
            .Select(product => product.Id).ToListAsync(ct)).ToHashSet();
        var rows = await db.Products.AsNoTracking().Where(product => ids.Contains(product.Id))
            .Select(product => new
            {
                product.Id, product.Slug, product.Name, product.Brand, product.ImageUrl, product.IsActive,
                Category = new CategoryDto(product.Category.Id, product.Category.Slug, product.Category.Name, product.Category.ParentCategoryId, product.Category.Level),
                IsCategoryActive = product.Category.IsActive,
                Minimum = VisibleCatalogOffers.Query(db, clock).Where(offer => offer.ProductId == product.Id).Select(offer => (int?)offer.PriceVnd).Min(),
                Maximum = VisibleCatalogOffers.Query(db, clock).Where(offer => offer.ProductId == product.Id).Select(offer => (int?)offer.PriceVnd).Max(),
                Count = VisibleCatalogOffers.Query(db, clock).Count(offer => offer.ProductId == product.Id)
            }).ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => new HomeShowcaseProductData(row.Id, row.Slug, row.Name, row.ImageUrl, row.Brand,
            row.Category, new OfferSummaryDto(row.Minimum, row.Maximum, row.Count), row.IsActive && row.IsCategoryActive && visibleIds.Contains(row.Id)));
    }

    public Task<bool> IsComputersCategoryAvailable(CancellationToken ct) => db.Categories.AsNoTracking().AnyAsync(category => category.Slug == "computers" && category.IsActive, ct);

    public async Task<bool> AreComputers(IReadOnlyCollection<int> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return true;
        var categoryIds = db.Categories.Where(category => category.Slug == "computers" && category.IsActive).Select(category => category.Id);
        return await db.Products.AsNoTracking().CountAsync(product => productIds.Contains(product.Id) && categoryIds.Contains(product.CategoryId), ct) == productIds.Count;
    }

    public async Task ReplaceSnapshot(HomeShowcaseSnapshot snapshot, IReadOnlyList<int?> featured, IReadOnlyList<int?> interested, CancellationToken ct)
    {
        var current = await db.HomeShowcaseSlots.Where(slot => slot.Snapshot == snapshot).ToListAsync(ct);
        db.HomeShowcaseSlots.RemoveRange(current);
        Add(HomeShowcaseSection.Featured, featured);
        Add(HomeShowcaseSection.InterestedComputers, interested);
        await db.SaveChangesAsync(ct);
        void Add(HomeShowcaseSection section, IReadOnlyList<int?> ids)
        {
            for (var index = 0; index < ids.Count; index++)
                db.HomeShowcaseSlots.Add(new HomeShowcaseSlot { Snapshot = snapshot, Section = section, SlotIndex = index + 1, ProductId = ids[index] });
        }
    }

    public async Task<IReadOnlyList<HomeShowcaseProduct>> PublicProducts(HomeShowcaseSection section, CancellationToken ct)
    {
        var ids = await db.HomeShowcaseSlots.AsNoTracking().Where(slot => slot.Snapshot == HomeShowcaseSnapshot.Published && slot.Section == section && slot.ProductId != null)
            .OrderBy(slot => slot.SlotIndex).Select(slot => slot.ProductId!.Value).ToListAsync(ct);
        if (ids.Count == 0) return [];
        var map = await Products(ids.Distinct().ToArray(), ct);
        var visible = BannedItemVisibility.ExcludeBannedProducts(db, db.Products.AsNoTracking().Where(product => ids.Contains(product.Id) && product.IsActive && product.Category.IsActive))
            .Select(product => product.Id);
        var visibleIds = await visible.ToListAsync(ct);
        return ids.Where(visibleIds.Contains).Where(map.ContainsKey).Distinct().Select(id => ToPublic(map[id])).Take(15).ToArray();
    }

    public async Task<IReadOnlyList<HomeShowcaseProduct>> LegacyFallback(HomeShowcaseSection section, CancellationToken ct)
    {
        int? categoryId = null;
        if (section == HomeShowcaseSection.InterestedComputers)
        {
            categoryId = await db.Categories.AsNoTracking().Where(category => category.Slug == "computers" && category.IsActive).Select(category => (int?)category.Id).FirstOrDefaultAsync(ct);
            if (categoryId is null) return [];
        }
        var page = await catalog.Products(new ProductFilter(1, 15, categoryId), ct);
        return page.Items.Select(product => new HomeShowcaseProduct(product.Id, product.Slug, product.Name, product.ImageUrl, product.Brand, product.Category, product.OfferSummary)).ToArray();
    }

    public Task<int> PreviewTokensCreatedBy(string actor, DateTime sinceUtc, CancellationToken ct) =>
        db.HomeShowcasePreviewTokens.CountAsync(token => token.ActorSubject == actor && token.CreatedAtUtc >= sinceUtc, ct);

    public async Task SavePreviewToken(HomeShowcasePreviewToken token, CancellationToken ct)
    {
        db.HomeShowcasePreviewTokens.Add(token);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ConsumePreviewToken(string tokenHash, DateTime nowUtc, CancellationToken ct)
    {
        return await db.HomeShowcasePreviewTokens.Where(item => item.TokenHash == tokenHash && item.ExpiresAtUtc > nowUtc && item.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ConsumedAtUtc, nowUtc), ct) == 1;
    }

    private static HomeShowcaseProduct ToPublic(HomeShowcaseProductData product) => new(product.Id, product.Slug, product.Name, product.ImageUrl, product.Brand, product.Category, product.OfferSummary);
}

public sealed class EfHomeShowcaseAudit(CompareShopDbContext db, TimeProvider clock) : IHomeShowcaseAudit
{
    public async Task<T> Execute<T>(string actor, string action, object? before, object? after, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await operation(ct);
        db.AdminAuditLogs.Add(new()
        {
            ActorSubject = actor, Action = action, ResourceType = "home_showcase", ResourceId = null,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(before), AfterJson = System.Text.Json.JsonSerializer.Serialize(after)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

}
