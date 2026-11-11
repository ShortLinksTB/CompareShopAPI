using System.Text.Json;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CompareShopAPI.Api.Admin;

/// <summary>Explicit, Development-only fixtures for SL-1815. Never called by HTTP startup.</summary>
public static class MatchingFixtureCommand
{
    private const string Owner = "QC-SL1815";
    private const string Prefix = "qc-sl1815-";
    private static readonly DateTime FixtureTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] is "seed-matching-fixtures" or "cleanup-matching-fixtures";

    public static async Task<bool> TryRunAsync(string[] args, IHostEnvironment environment, IConfiguration configuration, IServiceProvider services)
    {
        if (!IsRequested(args)) return false;
        if (!environment.IsDevelopment()) throw new InvalidOperationException("QC-SL1815 fixtures require ASPNETCORE_ENVIRONMENT=Development.");
        if (!configuration.GetValue<bool>("Database:AllowMatchingFixtureSeed")) throw new InvalidOperationException("Fixture command is disabled; explicitly set Database:AllowMatchingFixtureSeed=true for this one-off Job.");
        var cs = configuration.GetConnectionString("CompareShopDb");
        if (string.IsNullOrWhiteSpace(cs) || !string.Equals(new SqlConnectionStringBuilder(cs).InitialCatalog, "CompareShop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QC-SL1815 fixtures require a connection targeting only the CompareShop database.");

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        if (!await db.Database.CanConnectAsync()) throw new InvalidOperationException("CompareShop is unreachable; no fixture operation was performed.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var before = await Counts(db);
        var ids = args[0] == "seed-matching-fixtures" ? await Seed(db) : await Cleanup(db);
        await transaction.CommitAsync();
        var after = await Counts(db);
        Console.WriteLine(JsonSerializer.Serialize(new { owner = Owner, operation = args[0], before, ids, after }));
        return true;
    }

    private static async Task<object> Counts(CompareShopDbContext db) => new
    {
        products = await db.Products.CountAsync(), categories = await db.Categories.CountAsync(), matchings = await db.ProductMatchings.CountAsync(),
        offers = await db.ProductOffers.CountAsync(), mappings = await db.SourceItemMappings.CountAsync(), dataSources = await db.DataSources.CountAsync(),
        shops = await db.PartnerShops.CountAsync(), fixtureMatchingRows = await db.ProductMatchings.Where(x => x.Name.StartsWith(Owner)).CountAsync()
    };

    private static async Task<object> Seed(CompareShopDbContext db)
    {
        var existing = await db.DataSources.Where(x => x.Key.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();
        if (existing.Length > 0) return new { alreadySeeded = true, dataSourceIds = existing };

        var category = new Category { Slug = Prefix + "laptop", Name = Owner + " disposable laptop category", IsActive = true };
        var shopA = new PartnerShop { Slug = Prefix + "shop-a", Name = Owner + " Shop A", WebsiteUrl = "https://shop-a.example.test", IsActive = true };
        var shopB = new PartnerShop { Slug = Prefix + "shop-b", Name = Owner + " Shop B", WebsiteUrl = "https://shop-b.example.test", IsActive = true };
        db.AddRange(category, shopA, shopB);
        await db.SaveChangesAsync();

        var product = new Product { Slug = Prefix + "existing-product", Name = "ASUS VivoBook 15 X1504ZA Intel Core i5 8GB RAM 512GB SSD", Brand = "ASUS", CategoryId = category.Id, IsActive = true };
        db.Products.Add(product);
        var sourceA = new DataSource(Prefix + "shop-a", Owner + " Shop A", "/qc-sl1815/laptop", shopA.Id, FixtureTime);
        var sourceB = new DataSource(Prefix + "shop-b", Owner + " Shop B", "/qc-sl1815/laptop", shopB.Id, FixtureTime);
        var sourceNoShop = new DataSource(Prefix + "missing-shop", Owner + " Missing Shop", "/qc-sl1815/laptop", null, FixtureTime);
        db.DataSources.AddRange(sourceA, sourceB, sourceNoShop);
        await db.SaveChangesAsync();

        var offerA = new ProductOffer { ProductId = product.Id, PartnerShopId = shopA.Id, PriceVnd = 15990000, OriginalPriceVnd = 16990000, Url = "https://shop-a.example.test/qc-sl1815", ImageUrl = "https://shop-a.example.test/qc-sl1815.jpg", SpecificationsJson = "{\"ram\":\"8GB\",\"ssd\":\"512GB\"}", StockStatus = "in_stock", UpdatedAtUtc = FixtureTime, IsActive = true };
        var offerB = new ProductOffer { ProductId = product.Id, PartnerShopId = shopB.Id, PriceVnd = 16190000, Url = "https://shop-b.example.test/qc-sl1815", ImageUrl = "https://shop-b.example.test/qc-sl1815.jpg", StockStatus = "in_stock", UpdatedAtUtc = FixtureTime, IsActive = true };
        db.ProductOffers.AddRange(offerA, offerB);
        await db.SaveChangesAsync();

        var skuA = Owner + "-shop-a-laptop";
        var skuB = Owner + "-shop-b-laptop";
        var mappingA = new SourceItemMapping(sourceA.Id, skuA, product.Name, "ASUS"); mappingA.SetOffer(offerA.Id);
        mappingA.UpdateSnapshot(product.Name, "ASUS", new ScrapedOffer(skuA, product.Name, "ASUS", offerA.PriceVnd, offerA.Url!, offerA.ImageUrl, offerA.OriginalPriceVnd, new Dictionary<string,string> { ["ram"] = "8GB", ["ssd"] = "512GB" }, "in_stock"));
        var mappingB = new SourceItemMapping(sourceB.Id, skuB, product.Name, "ASUS"); mappingB.SetOffer(offerB.Id);
        mappingB.UpdateSnapshot(product.Name, "ASUS", new ScrapedOffer(skuB, product.Name, "ASUS", offerB.PriceVnd, offerB.Url!, offerB.ImageUrl, null, new Dictionary<string,string> { ["ram"] = "8GB", ["ssd"] = "512GB" }, "in_stock"));
        var skuNoShop = Owner + "-missing-shop-laptop";
        var mappingNoOffer = new SourceItemMapping(sourceNoShop.Id, skuNoShop, product.Name, "ASUS");
        mappingNoOffer.UpdateSnapshot(product.Name, "ASUS");
        var skuLinked = Owner + "-linked";
        var mappingLinked = new SourceItemMapping(sourceA.Id, skuLinked, product.Name, "ASUS"); mappingLinked.AssignProduct(product.Id);
        db.SourceItemMappings.AddRange(mappingA, mappingB, mappingNoOffer, mappingLinked);
        var mA = ProductMatching.ForSourceItem(product.Name, "ASUS", FixtureTime, sourceA.Id, skuA);
        var mB = ProductMatching.ForSourceItem(product.Name, "ASUS", FixtureTime, sourceB.Id, skuB);
        var nullable = new ProductMatching(Owner + " unlinked nullable relation case", null, FixtureTime);
        var missingMapping = ProductMatching.ForSourceItem(Owner + " missing mapping ASUS VivoBook 15 X1504ZA Intel Core i5 8GB RAM 512GB SSD", "ASUS", FixtureTime, sourceA.Id, Owner + "-missing-mapping");
        var linked = ProductMatching.ForSourceItem(Owner + " linked ASUS VivoBook 15 X1504ZA Intel Core i5 8GB RAM 512GB SSD", "ASUS", FixtureTime, sourceA.Id, skuLinked);
        linked.TryApprove(product.Id);
        var missingShopMatching = ProductMatching.ForSourceItem(product.Name, "ASUS", FixtureTime, sourceNoShop.Id, skuNoShop);
        var likelyNew = new ProductMatching(Owner + " NOVEL LAPTOP AuroraBook Z1 with 16GB RAM and 1TB SSD", "Aurora", FixtureTime);
        db.ProductMatchings.AddRange(mA, mB, nullable, missingMapping, linked, missingShopMatching, likelyNew);
        await db.SaveChangesAsync();
        return new { alreadySeeded = false, categoryIds = new[] { category.Id }, shopIds = new[] { shopA.Id, shopB.Id }, productIds = new[] { product.Id }, dataSourceIds = new[] { sourceA.Id, sourceB.Id, sourceNoShop.Id }, offerIds = new[] { offerA.Id, offerB.Id }, mappingIds = new[] { mappingA.Id, mappingB.Id, mappingNoOffer.Id, mappingLinked.Id }, matchingIds = new[] { mA.Id, mB.Id, nullable.Id, missingMapping.Id, linked.Id, missingShopMatching.Id, likelyNew.Id } };
    }

    private static async Task<object> Cleanup(CompareShopDbContext db)
    {
        var sourceIds = await db.DataSources.Where(x => x.Key.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();
        var matchingIds = await db.ProductMatchings.Where(x => x.Name.StartsWith(Owner) || (x.DataSourceId != null && sourceIds.Contains(x.DataSourceId.Value))).Select(x => x.Id).ToArrayAsync();
        var mappingIds = await db.SourceItemMappings.Where(x => sourceIds.Contains(x.DataSourceId) || x.ExternalSku.StartsWith(Owner)).Select(x => x.Id).ToArrayAsync();
        var offerIds = await db.ProductOffers.Where(x => x.Product.Slug.StartsWith(Prefix) || x.PartnerShop.Slug.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();
        var productIds = await db.Products.Where(x => x.Slug.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();
        var categoryIds = await db.Categories.Where(x => x.Slug.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();
        var shopIds = await db.PartnerShops.Where(x => x.Slug.StartsWith(Prefix)).Select(x => x.Id).ToArrayAsync();

        // Keep MatchingDecision/AdminAuditLog history. Delete only fixture-owned rows, from dependents to roots.
        await db.SourceItemMappings.Where(x => mappingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductMatchings.Where(x => matchingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductOffers.Where(x => offerIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Products.Where(x => productIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.DataSources.Where(x => sourceIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PartnerShops.Where(x => shopIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Categories.Where(x => categoryIds.Contains(x.Id)).ExecuteDeleteAsync();
        return new { categoryIds, shopIds, productIds, dataSourceIds = sourceIds, offerIds, mappingIds, matchingIds };
    }
}
