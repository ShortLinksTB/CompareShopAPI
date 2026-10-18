using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.Ingestion;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using Xunit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class IngestionMigrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Active_lease_blocks_overlap_and_expired_lease_is_failed_before_reclaim()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopLease_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            int sourceId;
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var source = new DataSource("bhx", "BHX", "/bia", null, now);
                setup.DataSources.Add(source);
                await setup.SaveChangesAsync();
                sourceId = source.Id;
            }

            SyncJob firstClaim;
            await using (var firstWorker = new CompareShopDbContext(options))
            {
                firstClaim = (await new EfSyncJobStore(firstWorker).TryClaim(sourceId, now, CancellationToken.None))!;
            }
            Assert.Equal(SyncJobState.Running, firstClaim.State);
            Assert.Equal(now.AddMinutes(SyncJob.LeaseDurationMinutes), firstClaim.LeaseExpiresAtUtc);

            await using (var concurrentWorker = new CompareShopDbContext(options))
            {
                var overlappingClaim = await new EfSyncJobStore(concurrentWorker).TryClaim(sourceId, now.AddMinutes(10), CancellationToken.None);
                Assert.Null(overlappingClaim);
            }

            var expiredAt = now.AddMinutes(SyncJob.LeaseDurationMinutes + 1);
            await using (var poll = new CompareShopDbContext(options))
                Assert.Contains(await new EfSyncJobStore(poll).DueSources(expiredAt, CancellationToken.None), source => source.Id == sourceId);

            SyncJob reclaimed;
            await using (var recoveryWorker = new CompareShopDbContext(options))
                reclaimed = (await new EfSyncJobStore(recoveryWorker).TryClaim(sourceId, expiredAt, CancellationToken.None))!;

            Assert.NotEqual(firstClaim.Id, reclaimed.Id);
            Assert.Equal(SyncJobState.Running, reclaimed.State);
            await using var verification = new CompareShopDbContext(options);
            var abandoned = await verification.SyncJobs.SingleAsync(job => job.Id == firstClaim.Id);
            Assert.Equal(SyncJobState.Failed, abandoned.State);
            Assert.Equal("lease_expired", abandoned.ErrorCode);
            Assert.Equal(expiredAt, abandoned.FinishedAtUtc);
            Assert.Null(abandoned.LeaseExpiresAtUtc);
            Assert.Equal(1, await verification.SyncJobs.CountAsync(job => job.DataSourceId == sourceId && job.State == SyncJobState.Running));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Migration_adds_only_ingestion_objects_and_seeds_bhx_without_requiring_shop_row()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopIngestion_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            var source = await db.DataSources.SingleAsync();
            Assert.Equal("bhx", source.Key);
            Assert.Equal("/bia", source.CategoryPath);
            Assert.Equal(72, source.IntervalHours);
            Assert.Null(source.PartnerShopId);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'SourceItemMappings'").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'SyncJobs') AND name = N'LeaseExpiresAtUtc'").SingleAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'ShopeeDataSources'").SingleAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Full_snapshot_deactivates_omitted_mapped_offer_even_with_unmapped_skus()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopIngestionSnapshot_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            int sourceId;
            int oldOfferId;
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var category = new Category { Slug = "test", Name = "Test" };
                var shop = new PartnerShop { Slug = "bhx", Name = "Bách Hóa Xanh" };
                setup.AddRange(category, shop);
                await setup.SaveChangesAsync();

                var product = new Product { Slug = "mapped-product", Name = "Mapped product", CategoryId = category.Id };
                var source = new DataSource("bhx", "Bách Hóa Xanh", "/bia", shop.Id, DateTime.UtcNow);
                setup.AddRange(product, source);
                await setup.SaveChangesAsync();

                var offer = new ProductOffer { ProductId = product.Id, PartnerShopId = shop.Id, PriceVnd = 10000, Url = "https://bhx.example/old", UpdatedAtUtc = DateTime.UtcNow };
                var mapping = new SourceItemMapping(source.Id, "mapped-sku", "Mapped product", null);
                mapping.Approve(product.Id);
                setup.AddRange(offer, mapping);
                await setup.SaveChangesAsync();
                mapping.SetOffer(offer.Id);
                await setup.SaveChangesAsync();
                sourceId = source.Id;
                oldOfferId = offer.Id;
            }

            await using (var db = new CompareShopDbContext(options))
            {
                var store = new EfSyncJobStore(db);
                var counts = await store.Import(sourceId,
                    [new ScrapedOffer("new-unmapped-sku", "Pending product", null, 12000, "https://bhx.example/new")],
                    fullSnapshot: true, CancellationToken.None);

                Assert.Equal(1, counts.Pending);
                Assert.False(await db.ProductOffers.Where(x => x.Id == oldOfferId).Select(x => x.IsActive).SingleAsync());
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
