using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class CatalogIntegrationTests(SqlServerTestFixture sqlServer)
{
    private static readonly TimeProvider FixedClock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Migration_guard_rejects_partial_history_table_without_changing_it()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopGuardTest_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync("20261017070000_AddCatalog");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.PriceHistoryPoints (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY)");

            var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());
            Assert.Contains("partial or incompatible", exception.Message);
            var columns = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PriceHistoryPoints')").SingleAsync();
            Assert.Equal(1, columns);
            Assert.True(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN (N'Products', N'PartnerShops')").SingleAsync() == 2);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Migration_seed_and_public_queries_are_idempotent_and_follow_catalog_rules()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopTest_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            db.PartnerShops.Add(new PartnerShop { Slug = "existing-unrelated-shop", Name = "Existing Shop", WebsiteUrl = "https://existing.example.test", IsActive = true });
            await db.SaveChangesAsync();
            await CatalogSeed.SeedAsync(db);
            var counts = (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync());
            await CatalogSeed.SeedAsync(db);
            Assert.Equal(counts, (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync()));

            var trackedOffer = await db.ProductOffers.OrderBy(x => x.Id).FirstAsync();
            var affiliateLink = new AffiliateLink
            {
                ProductOfferId = trackedOffer.Id,
                DestinationUrl = trackedOffer.Url!,
                IsEnabled = true,
                IsStale = false
            };
            db.AffiliateLinks.Add(affiliateLink);
            await db.SaveChangesAsync();
            var affiliateRepository = new EfAffiliateRedirectRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<EfAffiliateRedirectRepository>.Instance);
            Assert.NotNull(await affiliateRepository.FindRedirectableLink(trackedOffer.Id, default));
            Assert.True(await affiliateRepository.RecordClick(affiliateLink.Id, FixedClock.GetUtcNow().UtcDateTime, "hashed-client-ip", default));
            await db.Entry(affiliateLink).ReloadAsync();
            Assert.Equal(1, affiliateLink.ClickCount);
            Assert.Equal("hashed-client-ip", await db.AffiliateClicks.Select(x => x.ClientIpDigest).SingleAsync());

            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [FailAffiliateClickInsert] ON [AffiliateClicks] INSTEAD OF INSERT AS BEGIN RAISERROR('test click failure', 16, 1); END");
            Assert.False(await affiliateRepository.RecordClick(affiliateLink.Id, FixedClock.GetUtcNow().UtcDateTime, "second-hash", default));
            await db.Entry(affiliateLink).ReloadAsync();
            Assert.Equal(1, affiliateLink.ClickCount);
            Assert.Single(await db.AffiliateClicks.ToListAsync());
            db.ChangeTracker.Clear();
            Assert.All(await db.ProductOffers.Include(x => x.PartnerShop).Where(x => x.Product.Slug == "sample-product-01").ToListAsync(),
                offer => Assert.Contains(offer.PartnerShop.Slug, new[] { "north-shop", "central-store" }));

            var queries = new EfCatalogQueries(db, FixedClock);
            Assert.Equal(3, (await queries.Categories(default)).Count);
            var first = await queries.Products(new ProductFilter(Page: 1, PageSize: 5), default);
            var second = await queries.Products(new ProductFilter(Page: 2, PageSize: 5), default);
            Assert.Equal(30, first.TotalItems); Assert.Equal(6, first.TotalPages); Assert.Equal(5, second.Items.Count);
            Assert.True(string.CompareOrdinal(first.Items[^1].Name, second.Items[0].Name) < 0);
            Assert.Equal(first.Items.Select(x => x.Id), first.Items.OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => x.Id));

            var sample = await queries.Product("sample-product-01", default);
            Assert.NotNull(sample); Assert.Equal(2, sample.OfferSummary.OfferCount); Assert.Equal(1_010_000, sample.OfferSummary.MinPrice); Assert.Equal(1_060_000, sample.OfferSummary.MaxPrice);
            Assert.Equal(2, first.Items.Single(x => x.Slug == "sample-product-01").OfferSummary.OfferCount);
            Assert.Single(sample.SpecificationGroups); Assert.Equal("Example One", sample.SpecificationGroups[0].Items[0].Value);
            var offers = await queries.Offers(sample.Id, default); Assert.NotNull(offers); Assert.Equal(2, offers.Count);
            var noOffers = await queries.Product("sample-product-10", default);
            Assert.NotNull(noOffers); Assert.Equal(0, noOffers.OfferSummary.OfferCount); Assert.Null(noOffers.OfferSummary.MinPrice); Assert.Empty((await queries.Offers(noOffers.Id, default))!);
            Assert.Null(await queries.Product("inactive-sample-product", default)); Assert.Null(await queries.Product("missing", default));
            Assert.Null(await queries.Shop("missing", default)); Assert.Null(await queries.Shop("inactive-shop", default)); Assert.Null(await queries.Offers(int.MaxValue, default));
            Assert.NotNull(await queries.Shop("north-shop", default));
            Assert.Equal(2, (await queries.Product("sample-product-03", default))!.OfferSummary.OfferCount);

            var filtered = await queries.Products(new ProductFilter(CategoryId: first.Items[0].Category.Id, ShopId: offers[0].ShopId,
                MinPrice: 1_000_000, MaxPrice: 2_000_000, PageSize: 100), default);
            Assert.Contains(filtered.Items, x => x.Id == sample.Id);
            var empty = await queries.Products(new ProductFilter(MaxPrice: 1, PageSize: 10), default); Assert.Empty(empty.Items); Assert.Equal(0, empty.TotalItems);
            var staleOnlyRange = await queries.Products(new ProductFilter(MinPrice: 1, MaxPrice: 1, PageSize: 100), default);
            Assert.Empty(staleOnlyRange.Items); Assert.Equal(0, staleOnlyRange.TotalItems);
            var outOfRangePage = await queries.Products(new ProductFilter(Page: int.MaxValue, PageSize: 100), default);
            Assert.Empty(outOfRangePage.Items); Assert.Equal(30, outOfRangePage.TotalItems);

            var service = new CatalogService(queries);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(Page: 0), default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(PageSize: 101), default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(MinPrice: 2, MaxPrice: 1), default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(MinPrice: -1), default));
            var trimmedSearch = await service.Products(new ProductFilter(PageSize: 100, Search: "  Sample Product 01  "), default);
            Assert.Single(trimmedSearch.Items); Assert.Equal(sample.Id, trimmedSearch.Items[0].Id);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(Search: new string('x', 81)), default));
            var blankSearch = await service.Products(new ProductFilter(PageSize: 100, Search: "  "), default);
            Assert.Equal(30, blankSearch.TotalItems);
            var searchPage1 = await service.Products(new ProductFilter(Page: 1, PageSize: 3, Search: "Sample Product"), default);
            var searchPage2 = await service.Products(new ProductFilter(Page: 2, PageSize: 3, Search: "Sample Product"), default);
            Assert.Equal(30, searchPage1.TotalItems);
            Assert.True(string.CompareOrdinal(searchPage1.Items[^1].Name, searchPage2.Items[0].Name) < 0);
            var combinedSearch = await queries.Products(new ProductFilter(CategoryId: sample.Category.Id, ShopId: offers[0].ShopId,
                MinPrice: 1_000_000, MaxPrice: 2_000_000, PageSize: 100, Search: "product 01"), default);
            Assert.Contains(combinedSearch.Items, x => x.Id == sample.Id);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Products(new ProductFilter(Search: "x"), default));

            var collationCommand = db.Database.GetDbConnection().CreateCommand();
            collationCommand.CommandText = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";
            await db.Database.OpenConnectionAsync();
            var collation = (string)(await collationCommand.ExecuteScalarAsync())!;
            await db.Database.CloseConnectionAsync();
            var caseSearch = await queries.Products(new ProductFilter(PageSize: 100, Search: "sample product 01"), default);
            if (collation.Contains("_CI_", StringComparison.OrdinalIgnoreCase)) Assert.Contains(caseSearch.Items, x => x.Id == sample.Id);
            else Assert.DoesNotContain(caseSearch.Items, x => x.Id == sample.Id);

            var prefixSuggestion = new Product { Slug = "unique-prefix", Name = "Unique Search Product", CategoryId = sample.Category.Id, IsActive = true };
            var containsSuggestion = new Product { Slug = "unique-contains", Name = "A Unique Search Product", CategoryId = sample.Category.Id, IsActive = true };
            db.Products.AddRange(prefixSuggestion, containsSuggestion);
            await db.SaveChangesAsync();
            var suggestions = await queries.Suggestions("unique search", 10, default);
            Assert.Equal(new[] { prefixSuggestion.Id, containsSuggestion.Id }, suggestions.Select(x => x.Id));
            Assert.Empty(await queries.Suggestions("no such match", 10, default));
            await Assert.ThrowsAsync<ArgumentException>(() => service.Suggestions(" ", 8, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Suggestions("unique", 11, default));
            await Assert.ThrowsAsync<ArgumentException>(() => service.Suggestions(new string('x', 81), 8, default));

            var historyStore = new EfPriceHistoryStore(db, FixedClock);
            var historyService = new PriceHistoryService(historyStore, FixedClock);
            Assert.Empty((await historyService.History(noOffers.Id, null, null, default))!);
            Assert.Null(await historyService.History(int.MaxValue, null, null, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => historyService.History(0, null, null, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => historyService.RecordMinimum(0, default));
            var inactiveId = await db.Products.Where(product => product.Slug == "inactive-sample-product").Select(product => product.Id).SingleAsync();
            Assert.Null(await historyService.History(inactiveId, null, null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => historyService.History(sample.Id, "2026-10-01T00:00:00+07:00", null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => historyService.History(sample.Id, "2026-10-01", null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => historyService.History(sample.Id, "2025-01-01T00:00:00Z", "2026-02-01T00:00:00Z", default));
            await Assert.ThrowsAsync<ArgumentException>(() => historyService.History(sample.Id, "2026-10-02T00:00:00Z", "2026-10-01T00:00:00Z", default));
            db.PriceHistoryPoints.AddRange(
                PriceHistoryPoint.Create(sample.Id, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 1_040_000, null),
                PriceHistoryPoint.Create(sample.Id, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1_020_000, null),
                PriceHistoryPoint.Create(sample.Id, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), 1_030_000, offers[0].ShopId));
            await db.SaveChangesAsync();
            var orderedHistory = await historyService.History(sample.Id, null, null, default);
            Assert.Equal(new[] { 1_030_000, 1_020_000 }, orderedHistory!.Select(point => point.MinPrice));
            Assert.Empty((await historyService.History(sample.Id, null, "2026-05-01T00:00:00Z", default))!);
            Assert.Single((await historyService.History(sample.Id, "2026-09-01T00:00:00Z", null, default))!);
            Assert.Null(orderedHistory![^1].ShopId);
            var storedHistory = await db.PriceHistoryPoints.Include(point => point.Product).Include(point => point.PartnerShop)
                .Where(point => point.ProductId == sample.Id && point.DateUtc >= new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))
                .OrderBy(point => point.DateUtc).ToListAsync();
            Assert.True(storedHistory[0].Id > 0);
            Assert.Equal(sample.Id, storedHistory[0].Product.Id);
            Assert.Equal(offers[0].ShopId, storedHistory[0].PartnerShop!.Id);
            Assert.Null(storedHistory[1].PartnerShop);
            var beforeRecording = await db.PriceHistoryPoints.CountAsync(point => point.ProductId == sample.Id);
            await historyService.RecordMinimum(sample.Id, default);
            await historyService.RecordMinimum(sample.Id, default);
            Assert.Equal(beforeRecording + 1, await db.PriceHistoryPoints.CountAsync(point => point.ProductId == sample.Id));
            await historyService.RecordMinimum(noOffers.Id, default);
            Assert.Equal(0, await db.PriceHistoryPoints.CountAsync(point => point.ProductId == noOffers.Id));
            var freshnessCutoff = FixedClock.GetUtcNow().UtcDateTime.AddDays(-CatalogOfferPolicy.FreshnessDays);
            var currentMinimumOffer = await db.ProductOffers.Where(offer => offer.ProductId == sample.Id && offer.IsActive && !offer.IsOutlier &&
                    offer.UpdatedAtUtc >= freshnessCutoff && offer.PartnerShop.IsActive)
                .OrderBy(offer => offer.PriceVnd).FirstAsync();
            currentMinimumOffer.PriceVnd = 900_000;
            await db.SaveChangesAsync();
            await historyService.RecordMinimum(sample.Id, default);
            Assert.Equal(beforeRecording + 2, await db.PriceHistoryPoints.CountAsync(point => point.ProductId == sample.Id));
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceHistoryPoint.Create(0, DateTime.UtcNow, 10, null));
            Assert.Throws<ArgumentException>(() => PriceHistoryPoint.Create(sample.Id, DateTime.Now, 10, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceHistoryPoint.Create(sample.Id, DateTime.UtcNow, -1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceHistoryPoint.Create(sample.Id, DateTime.UtcNow, 10, 0));

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true, timeProvider: FixedClock);
            var client = app.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/categories")).StatusCode);
            var response = await client.GetAsync("/api/v1/products?page=1&pageSize=5&categoryId=1&minPrice=1000000&maxPrice=2000000&shopId=1");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/products?page=0")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/products?search=x")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/products?search=%20Sample%20Product%2001%20")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/search/suggestions?q=x")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/search/suggestions?q=unique&limit=11")).StatusCode);
            var suggestionResponse = await client.GetAsync("/api/v1/search/suggestions?q=unique%20search");
            Assert.Equal(System.Net.HttpStatusCode.OK, suggestionResponse.StatusCode);
            using (var suggestionJson = System.Text.Json.JsonDocument.Parse(await suggestionResponse.Content.ReadAsStringAsync()))
            {
                var suggestionItems = suggestionJson.RootElement.GetProperty("items");
                Assert.Equal(prefixSuggestion.Id, suggestionItems[0].GetProperty("id").GetInt32());
                Assert.Equal(containsSuggestion.Id, suggestionItems[1].GetProperty("id").GetInt32());
            }
            var emptySuggestionResponse = await client.GetAsync("/api/v1/search/suggestions?q=missing%20thing");
            using (var emptySuggestionJson = System.Text.Json.JsonDocument.Parse(await emptySuggestionResponse.Content.ReadAsStringAsync()))
                Assert.Empty(emptySuggestionJson.RootElement.GetProperty("items").EnumerateArray());
            var emptyHistoryResponse = await client.GetAsync($"/api/v1/products/{noOffers.Id}/price-history");
            Assert.Equal(System.Net.HttpStatusCode.OK, emptyHistoryResponse.StatusCode);
            var historyResponse = await client.GetAsync($"/api/v1/products/{sample.Id}/price-history?from=2026-07-01T00:00:00Z&to=2026-10-17T00:00:00Z");
            Assert.Equal(System.Net.HttpStatusCode.OK, historyResponse.StatusCode);
            using (var historyJson = System.Text.Json.JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync()))
                Assert.Equal(new[] { 1_030_000, 1_020_000, 1_010_000, 900_000 }, historyJson.RootElement.GetProperty("items").EnumerateArray().Select(point => point.GetProperty("minPrice").GetInt32()));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/products/{sample.Id}/price-history?from=2025-01-01T00:00:00Z&to=2026-02-01T00:00:00Z")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/products/{sample.Id}/price-history?from=2026-10-01")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/products/2147483647/price-history")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{inactiveId}/price-history")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/products/sample-product-01")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/products/inactive-sample-product")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync($"/api/v1/products/{noOffers.Id}/offers")).StatusCode);
            var offerResponse = await client.GetAsync($"/api/v1/products/{sample.Id}/offers");
            Assert.Equal(System.Net.HttpStatusCode.OK, offerResponse.StatusCode);
            using var offerJson = System.Text.Json.JsonDocument.Parse(await offerResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, offerJson.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/products/2147483647/offers")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/shops/north-shop")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/shops/missing")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/wishlist")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(subject: "regular-user-a"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/wishlist", System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, operation = "merge" }))).StatusCode);
            var wishlistMutation = System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, operation = "add" });
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/wishlist", wishlistMutation)).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/wishlist", System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, operation = "add" }))).StatusCode);
            using var wishlist = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/api/v1/wishlist")).Content.ReadAsStringAsync());
            Assert.Single(wishlist.RootElement.GetProperty("items").EnumerateArray());

            var createAlert = await client.PostAsync("/api/v1/price-alerts", System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, targetPrice = 999_000 }));
            Assert.Equal(System.Net.HttpStatusCode.Created, createAlert.StatusCode);
            using var alertJson = System.Text.Json.JsonDocument.Parse(await createAlert.Content.ReadAsStringAsync());
            var alertId = alertJson.RootElement.GetProperty("id").GetInt32();
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/price-alerts", System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, targetPrice = 0 }))).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(subject: "regular-user-b"));
            using var isolatedAlerts = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/api/v1/price-alerts")).Content.ReadAsStringAsync());
            Assert.Empty(isolatedAlerts.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/price-alerts/{alertId}")).StatusCode);
            using var isolatedWishlist = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/api/v1/wishlist")).Content.ReadAsStringAsync());
            Assert.Empty(isolatedWishlist.RootElement.GetProperty("items").EnumerateArray());
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(subject: "regular-user-a"));
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/price-alerts/{alertId}")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/wishlist", System.Net.Http.Json.JsonContent.Create(new { productId = sample.Id, operation = "remove" }))).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/price-alerts", System.Net.Http.Json.JsonContent.Create(new { productId = int.MaxValue, targetPrice = 100 }))).StatusCode);
            await db.Database.MigrateAsync("20261017070000_AddCatalog");
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'PriceHistoryPoints'").SingleAsync());
            Assert.Equal(6, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN (N'Categories', N'PartnerShops', N'Products', N'ProductOffers', N'SpecificationGroups', N'SpecificationItems')").SingleAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
