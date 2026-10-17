using CompareShopAPI.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Persistence;

/// <summary>Explicit dev/test seed. Never invoked by application startup.</summary>
public static class CatalogSeed
{
    public static async Task SeedAsync(CompareShopDbContext db, CancellationToken cancellationToken = default)
    {
        var categoryNames = new[] { "Phones", "Computers", "Audio" };
        foreach (var name in categoryNames)
        {
            var slug = name.ToLowerInvariant();
            if (!await db.Categories.AnyAsync(x => x.Slug == slug, cancellationToken))
                db.Categories.Add(new Category { Slug = slug, Name = name, IsActive = true });
        }
        var shopNames = new[] { "North Shop", "Central Store", "South Market" };
        foreach (var name in shopNames)
        {
            var slug = name.ToLowerInvariant().Replace(' ', '-');
            if (!await db.PartnerShops.AnyAsync(x => x.Slug == slug, cancellationToken))
                db.PartnerShops.Add(new PartnerShop { Slug = slug, Name = name, WebsiteUrl = $"https://{slug}.example.test", IsActive = true });
        }
        if (!await db.PartnerShops.AnyAsync(x => x.Slug == "inactive-shop", cancellationToken))
            db.PartnerShops.Add(new PartnerShop { Slug = "inactive-shop", Name = "Inactive Shop", IsActive = false });
        await db.SaveChangesAsync(cancellationToken);
        var categoryIds = await db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Slug, x => x.Id, cancellationToken);
        var shops = await db.PartnerShops.OrderBy(x => x.Id).Take(3).ToListAsync(cancellationToken);
        for (var i = 1; i <= 30; i++)
        {
            var slug = $"sample-product-{i:00}";
            var product = await db.Products.SingleOrDefaultAsync(x => x.Slug == slug, cancellationToken);
            if (product is null)
            {
                var category = categoryIds[categoryNames[(i - 1) % categoryNames.Length].ToLowerInvariant()];
                product = new Product { Slug = slug, Name = $"Sample Product {i:00}", Brand = "Example", Description = "Generated catalog fixture", CategoryId = category, IsActive = true };
                db.Products.Add(product);
                await db.SaveChangesAsync(cancellationToken);
            }
            if (i % 10 != 0 && !await db.ProductOffers.AnyAsync(x => x.ProductId == product.Id, cancellationToken))
            {
                for (var shopIndex = 0; shopIndex < 2; shopIndex++)
                    db.ProductOffers.Add(new ProductOffer { ProductId = product.Id, PartnerShopId = shops[shopIndex].Id, PriceVnd = 1_000_000 + i * 10_000 + shopIndex * 50_000, Url = $"https://example.test/{slug}/{shopIndex}", UpdatedAtUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), IsActive = true });
            }
            if (i == 1 && !await db.SpecificationGroups.AnyAsync(x => x.ProductId == product.Id, cancellationToken))
            {
                var group = new SpecificationGroup { ProductId = product.Id, Name = "General", SortOrder = 0 };
                group.Items.Add(new SpecificationItem { Name = "Model", Value = "Example One", SortOrder = 0 });
                db.SpecificationGroups.Add(group);
            }
        }
        var inactive = await db.Products.SingleOrDefaultAsync(x => x.Slug == "inactive-sample-product", cancellationToken);
        if (inactive is null)
        {
            inactive = new Product { Slug = "inactive-sample-product", Name = "Inactive Sample Product", CategoryId = categoryIds["phones"], IsActive = false };
            db.Products.Add(inactive);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (!await db.ProductOffers.AnyAsync(x => x.ProductId == inactive.Id, cancellationToken))
            db.ProductOffers.Add(new ProductOffer { ProductId = inactive.Id, PartnerShopId = shops[0].Id, PriceVnd = 1, UpdatedAtUtc = DateTime.UnixEpoch, IsActive = true });
        if (!await db.ProductOffers.AnyAsync(x => x.IsOutlier, cancellationToken))
            db.ProductOffers.Add(new ProductOffer { ProductId = (await db.Products.SingleAsync(x => x.Slug == "sample-product-01", cancellationToken)).Id, PartnerShopId = shops[0].Id, PriceVnd = 1, UpdatedAtUtc = DateTime.UnixEpoch, IsActive = true, IsOutlier = true });
        if (!await db.ProductOffers.AnyAsync(x => !x.IsActive, cancellationToken))
            db.ProductOffers.Add(new ProductOffer { ProductId = (await db.Products.SingleAsync(x => x.Slug == "sample-product-02", cancellationToken)).Id, PartnerShopId = shops[0].Id, PriceVnd = 1, UpdatedAtUtc = DateTime.UnixEpoch, IsActive = false });
        if (!await db.ProductOffers.AnyAsync(x => !x.PartnerShop.IsActive, cancellationToken))
            db.ProductOffers.Add(new ProductOffer { ProductId = (await db.Products.SingleAsync(x => x.Slug == "sample-product-03", cancellationToken)).Id, PartnerShopId = await db.PartnerShops.Where(x => x.Slug == "inactive-shop").Select(x => x.Id).SingleAsync(cancellationToken), PriceVnd = 1, UpdatedAtUtc = DateTime.UnixEpoch, IsActive = true });
        var firstProductId = await db.Products.Where(x => x.Slug == "sample-product-01").Select(x => x.Id).SingleAsync(cancellationToken);
        var staleUpdatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        if (!await db.ProductOffers.AnyAsync(x => x.ProductId == firstProductId && x.UpdatedAtUtc == staleUpdatedAt, cancellationToken))
            db.ProductOffers.Add(new ProductOffer { ProductId = firstProductId, PartnerShopId = shops[0].Id, PriceVnd = 1, UpdatedAtUtc = staleUpdatedAt, IsActive = true });
        await db.SaveChangesAsync(cancellationToken);
    }
}
