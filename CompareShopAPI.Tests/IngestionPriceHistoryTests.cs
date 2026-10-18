using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class IngestionPriceHistoryTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task RecorderStoresOnlyChangedFreshMinimums()
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopPriceHistory_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var category = new Category { Slug = "history-test", Name = "History" };
            var shop = new PartnerShop { Slug = "history-shop", Name = "History Shop" };
            var product = new Product { Slug = "history-product", Name = "History Product", Category = category };
            db.AddRange(category, shop, product,
                new ProductOffer { Product = product, PartnerShop = shop, PriceVnd = 12000, UpdatedAtUtc = now },
                new ProductOffer { Product = product, PartnerShop = shop, PriceVnd = 10000, UpdatedAtUtc = now, IsOutlier = true },
                new ProductOffer { Product = product, PartnerShop = shop, PriceVnd = 9000, UpdatedAtUtc = now.AddDays(-31) });
            await db.SaveChangesAsync();

            var recorder = new EfPriceHistoryRecorder(db, new FixedClock(now));
            await recorder.RecordForChangedMinimums(CancellationToken.None);
            var point = await db.PriceHistoryPoints.SingleAsync();
            Assert.Equal(12000, point.MinPriceVnd);
            Assert.Equal(shop.Id, point.PartnerShopId);

            await recorder.RecordForChangedMinimums(CancellationToken.None);
            Assert.Equal(1, await db.PriceHistoryPoints.CountAsync());
            var offer = await db.ProductOffers.SingleAsync(x => x.PriceVnd == 12000);
            offer.PriceVnd = 11000;
            await db.SaveChangesAsync();
            await recorder.RecordForChangedMinimums(CancellationToken.None);
            Assert.Equal([12000, 11000], await db.PriceHistoryPoints.OrderBy(x => x.Id).Select(x => x.MinPriceVnd).ToArrayAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
