using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CatalogIntegrationTests
{
    private static readonly TimeProvider FixedClock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Migration_seed_and_public_queries_are_idempotent_and_follow_catalog_rules()
    {
        var raw = Environment.GetEnvironmentVariable("COMPARESHOP_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(raw), "MSSQL test connection is required; this test must not silently skip.");
        var connection = new SqlConnectionStringBuilder(raw) { InitialCatalog = $"CompareShopTest_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            await CatalogSeed.SeedAsync(db);
            var counts = (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync());
            await CatalogSeed.SeedAsync(db);
            Assert.Equal(counts, (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync()));

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

            await using var app = new ApiFactory(settings: new()
            {
                ["ConnectionStrings:CompareShopDb"] = connection,
                ["CatalogSeed:Enabled"] = "true",
                ["CatalogSeed:Environment"] = "dev"
            }, signing: true, timeProvider: FixedClock);
            var client = app.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/categories")).StatusCode);
            var response = await client.GetAsync("/api/v1/products?page=1&pageSize=5&categoryId=1&minPrice=1000000&maxPrice=2000000&shopId=1");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/products?page=0")).StatusCode);
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

            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}"));
            var seededCounts = (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync());
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/admin/dev/catalog-seed", null)).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/admin/dev/catalog-seed", null)).StatusCode);
            Assert.Equal(seededCounts, (await db.Products.CountAsync(), await db.Categories.CountAsync(), await db.PartnerShops.CountAsync(), await db.ProductOffers.CountAsync()));
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
