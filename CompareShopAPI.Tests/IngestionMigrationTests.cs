using CompareShopAPI.Application.Ingestion;
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
            var source = await db.DataSources.SingleAsync(x => x.Key == "bhx");
            Assert.Equal("bhx", source.Key);
            Assert.Equal("/bia", source.CategoryPath);
            Assert.Equal(72, source.IntervalHours);
            Assert.Null(source.PartnerShopId);
            var cellphones = await db.DataSources.SingleAsync(x => x.Key == "cellphones");
            Assert.Equal("/laptop/mac.html", cellphones.CategoryPath);
            Assert.Equal("cellphones", await db.PartnerShops.Where(x => x.Id == cellphones.PartnerShopId).Select(x => x.Slug).SingleAsync());
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
    public async Task Blocked_source_completion_persists_pause_and_sanitized_error()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopBlocked_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var now = DateTime.UtcNow;
        try
        {
            int sourceId;
            SyncJob job;
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var source = new DataSource("bhx", "BHX", "/bia", null, now);
                setup.DataSources.Add(source);
                await setup.SaveChangesAsync();
                sourceId = source.Id;
                job = (await new EfSyncJobStore(setup).TryClaim(sourceId, now, CancellationToken.None))!;
            }

            await using (var db = new CompareShopDbContext(options))
                await new EfSyncJobStore(db).Complete(sourceId, job, SyncJobState.Failed, now.AddMinutes(1), 0, 0, 0,
                    "source_blocked", SourceAccessBlockedException.SanitizedSummary, CancellationToken.None, pauseSource: true);

            await using var verification = new CompareShopDbContext(options);
            var persistedSource = await verification.DataSources.SingleAsync(x => x.Id == sourceId);
            var persistedJob = await verification.SyncJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(DataSourceState.Paused, persistedSource.State);
            Assert.Null(persistedSource.NextAttemptAtUtc);
            Assert.Equal("source_blocked", persistedJob.ErrorCode);
            Assert.Equal(SourceAccessBlockedException.SanitizedSummary, persistedJob.ErrorSummary);
            Assert.Empty(await new EfSyncJobStore(verification).DueSources(now.AddDays(365), CancellationToken.None));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Failed_source_completion_schedules_retry_when_source_is_not_paused()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopRetry_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var now = DateTime.UtcNow;
        try
        {
            int sourceId;
            SyncJob job;
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var source = new DataSource("bhx", "BHX", "/bia", null, now);
                setup.DataSources.Add(source);
                await setup.SaveChangesAsync();
                sourceId = source.Id;
                job = (await new EfSyncJobStore(setup).TryClaim(sourceId, now, CancellationToken.None))!;
            }

            var finished = now.AddMinutes(1);
            await using (var db = new CompareShopDbContext(options))
                await new EfSyncJobStore(db).Complete(sourceId, job, SyncJobState.Failed, finished, 0, 0, 0,
                    "temporary_error", "temporary failure", CancellationToken.None);

            await using var verification = new CompareShopDbContext(options);
            var sourceAfterFailure = await verification.DataSources.SingleAsync(x => x.Id == sourceId);
            Assert.Equal(DataSourceState.Active, sourceAfterFailure.State);
            Assert.Equal<DateTime?>(finished.AddHours(6), sourceAfterFailure.NextAttemptAtUtc);
            var store = new EfSyncJobStore(verification);
            Assert.Empty(await store.DueSources(finished.AddMinutes(5), default));
            Assert.Null(await store.TryClaim(sourceId, finished.AddHours(6).AddTicks(-1), default));
            Assert.Single(await store.DueSources(finished.AddHours(6), default));
            var retry = await store.TryClaim(sourceId, finished.AddHours(6), default);
            Assert.NotNull(retry);
            Assert.Empty(await store.DueSources(finished.AddHours(6).AddMinutes(5), default));
            await store.Complete(sourceId, retry!, SyncJobState.Success, finished.AddHours(6).AddMinutes(1), 1, 1, 0, null, null, default);
            Assert.Empty(await store.DueSources(finished.AddHours(78), default));
            Assert.Single(await store.DueSources(finished.AddHours(78).AddMinutes(1), default));
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
                    [new ScrapedOffer("mapped-sku", "Mapped product", null, 11000, "https://bhx.example/mapped", "https://images.bhx.example/item.jpg", 12000, new Dictionary<string,string> { ["memory"] = "16GB" }, "in_stock"),
                     new ScrapedOffer("new-unmapped-sku", "Pending product", null, 12000, "https://bhx.example/new", "https://images.bhx.example/pending.jpg", 13000, new Dictionary<string,string> { ["storage"] = "512GB" }, "coming_soon")],
                    fullSnapshot: true, CancellationToken.None);

                Assert.Equal(1, counts.Imported);
                Assert.Equal(1, counts.Pending);
                var updated = await db.ProductOffers.SingleAsync(x => x.Id == oldOfferId);
                Assert.True(updated.IsActive);
                Assert.Equal(12000, updated.OriginalPriceVnd);
                Assert.Equal("https://images.bhx.example/item.jpg", updated.ImageUrl);
                Assert.Equal("in_stock", updated.StockStatus);
                Assert.Contains("16GB", updated.SpecificationsJson);
                var snapshot = await db.ProductOfferSnapshots.SingleAsync(x => x.ProductOfferId == oldOfferId);
                Assert.Equal(10000, snapshot.PriceVnd);
                Assert.Equal("https://bhx.example/old", snapshot.Url);
                var pending = await db.SourceItemMappings.SingleAsync(x => x.ExternalSku == "new-unmapped-sku");
                Assert.Null(pending.ProductId);
                Assert.Equal("https://images.bhx.example/pending.jpg", pending.ImageUrl);
                Assert.Equal(13000, pending.OriginalPriceVnd);
                Assert.Contains("512GB", pending.SpecificationsJson);
                Assert.Equal("coming_soon", pending.StockStatus);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task CellphoneS_import_is_not_attempted_without_its_partner_shop()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopMissingShop_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var bhxShop = new PartnerShop { Slug = "bhx", Name = "Bách Hóa Xanh" };
            var source = new DataSource("cellphones", "CellphoneS", "/laptop/mac.html", null, DateTime.UtcNow);
            var bhx = new DataSource("bhx", "Bách Hóa Xanh", "/bia", null, DateTime.UtcNow);
            db.AddRange(bhxShop, source, bhx);
            await db.SaveChangesAsync();
            var store = new EfSyncJobStore(db);
            Assert.False(await store.ShopIsUsable(source.Id, default));
            Assert.True(await store.ShopIsUsable(bhx.Id, default));
            Assert.Equal(bhxShop.Id, bhx.PartnerShopId);
            Assert.Empty(await db.SourceItemMappings.ToListAsync());
            Assert.Empty(await db.ProductOffers.ToListAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task CellphoneS_migration_repairs_partial_offer_objects_and_reuses_existing_source_rows()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopPartialCellphoneS_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync("20261018140000_AddBhxIngestion");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO [PartnerShops] ([Slug],[Name],[WebsiteUrl],[IsActive]) VALUES (N'cellphones',N'CellphoneS',N'https://cellphones.com.vn',1);
                INSERT INTO [PartnerShops] ([Slug],[Name],[WebsiteUrl],[IsActive]) VALUES (N'wrong-shop',N'Wrong shop',N'https://wrong.example',1);
                DECLARE @WrongShopId int = (SELECT [Id] FROM [PartnerShops] WHERE [Slug] = N'wrong-shop');
                INSERT INTO [DataSources] ([Key],[DisplayName],[CategoryPath],[PartnerShopId],[State],[IntervalHours],[CreatedAtUtc]) VALUES (N'cellphones',N'Existing CellphoneS',N'/laptop/mac.html',@WrongShopId,N'Active',72,SYSUTCDATETIME());
                ALTER TABLE [ProductOffers] ADD [ImageUrl] nvarchar(2048) NULL;
                CREATE TABLE [ProductOfferSnapshots] ([Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, [ProductOfferId] int NULL);
                """);
            await db.Database.MigrateAsync();
            var source = await db.DataSources.SingleAsync(x => x.Key == "cellphones");
            Assert.Equal("/laptop/mac.html", source.CategoryPath);
            Assert.Equal("cellphones", await db.PartnerShops.Where(x => x.Id == source.PartnerShopId).Select(x => x.Slug).SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ProductOfferSnapshots') AND name = N'StockStatus'").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = N'FK_ProductOfferSnapshots_ProductOffers_ProductOfferId'").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [DataSources] WHERE [Key] = N'cellphones'").SingleAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Category_registration_is_additive_and_preserves_the_existing_mac_identity_and_relation()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopCellphoneSCategories_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            int macId;
            int originalPartnerShopId;
            const string originalPath = "/laptop/mac.html";
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync("20261027150000_AddCellphoneSOfferData");
                var cellphonesShopId = await setup.PartnerShops.Where(x => x.Slug == "cellphones").Select(x => x.Id).SingleAsync();
                var otherShop = new PartnerShop { Slug = $"other-{Guid.NewGuid():N}", Name = "Other shop", WebsiteUrl = "https://other.example", IsActive = true };
                setup.PartnerShops.Add(otherShop);
                await setup.SaveChangesAsync();
                var mac = await setup.DataSources.SingleAsync(x => x.Key == "cellphones");
                macId = mac.Id;
                originalPartnerShopId = otherShop.Id;
                mac.AttachPartnerShop(originalPartnerShopId);
                await setup.SaveChangesAsync();
                Assert.NotEqual(cellphonesShopId, originalPartnerShopId);
                Assert.Equal(originalPath, mac.CategoryPath);
            }

            await using (var migrate = new CompareShopDbContext(options))
            {
                await migrate.Database.MigrateAsync();
                await migrate.Database.MigrateAsync();
                var mac = await migrate.DataSources.SingleAsync(x => x.Key == "cellphones");
                Assert.Equal(macId, mac.Id);
                Assert.Equal(originalPartnerShopId, mac.PartnerShopId);
                Assert.Equal(originalPath, mac.CategoryPath);

                var cellphonesShopId = await migrate.PartnerShops.Where(x => x.Slug == "cellphones").Select(x => x.Id).SingleAsync();
                var categories = await migrate.DataSources.Where(x => x.Key.StartsWith("cellphones-")).ToListAsync();
                Assert.Equal(5, categories.Count);
                Assert.All(categories, source => Assert.Equal(cellphonesShopId, source.PartnerShopId));
                Assert.Equal(1, await migrate.DataSources.CountAsync(x => x.Key == "cellphones-asus"));
                Assert.Equal("/laptop/acer.html", (await migrate.DataSources.SingleAsync(x => x.Key == "cellphones-acer")).CategoryPath);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Category_registration_fails_safely_when_cellphones_shop_is_missing()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopCellphoneSShopGate_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync("20261027150000_AddCellphoneSOfferData");
                await setup.Database.ExecuteSqlRawAsync("UPDATE [PartnerShops] SET [Slug] = N'renamed-cellphones' WHERE [Slug] = N'cellphones'");

                await Assert.ThrowsAsync<SqlException>(() => setup.Database.MigrateAsync());
                Assert.Equal(0, await setup.DataSources.CountAsync(x => x.Key.StartsWith("cellphones-")));
                Assert.Equal(1, await setup.DataSources.CountAsync(x => x.Key == "cellphones"));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Theory]
    [InlineData("ProductOffers")]
    [InlineData("SourceItemMappings")]
    [InlineData("PartnerShops")]
    [InlineData("DataSources")]
    public async Task CellphoneS_migration_fails_and_is_not_recorded_when_a_required_table_is_missing(string missingTable)
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopMissing_{missingTable}_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync("20261018140000_AddBhxIngestion");
            var dropSql = $"""
                DECLARE @dropConstraints nvarchar(max) = N'';
                SELECT @dropConstraints += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME([parent_object_id])) + N'.' + QUOTENAME(OBJECT_NAME([parent_object_id])) + N' DROP CONSTRAINT ' + QUOTENAME([name]) + N';'
                FROM sys.foreign_keys WHERE [referenced_object_id] = OBJECT_ID(N'dbo.{missingTable}');
                EXEC sp_executesql @dropConstraints;
                DROP TABLE [dbo].[{missingTable}];
                """;
            await db.Database.ExecuteSqlRawAsync(dropSql);

            await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261027150000_AddCellphoneSOfferData'").SingleAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'ProductOfferSnapshots'").SingleAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
