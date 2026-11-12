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
    private const string ManifestActionSeeded = "fixture.manifest.seeded";
    private const string ManifestActionCleaned = "fixture.manifest.cleaned";
    private const string ManifestResourceType = "QC-SL1815-MatchingFixture";
    private static readonly DateTime FixtureTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] is "seed-matching-fixtures" or "cleanup-matching-fixtures";

    public static async Task<bool> TryRunAsync(string[] args, IHostEnvironment environment, IConfiguration configuration, IServiceProvider services)
    {
        if (!IsRequested(args)) return false;
        if (!environment.IsDevelopment()) throw new InvalidOperationException("QC-SL1815 fixtures require ASPNETCORE_ENVIRONMENT=Development.");
        if (!configuration.GetValue<bool>("Database:AllowMatchingFixtureSeed")) throw new InvalidOperationException("Fixture command is disabled; explicitly set Database:AllowMatchingFixtureSeed=true for this one-off Job.");
        var cs = configuration.GetConnectionString("CompareShopDb");
        var allowedServer = configuration["Database:MatchingFixtureAllowedServer"]?.Trim();
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(allowedServer))
            throw new InvalidOperationException("QC-SL1815 fixtures require an explicit dev SQL server allowlist and CompareShop connection.");
        var target = new SqlConnectionStringBuilder(cs);
        if (!string.Equals(target.InitialCatalog, "CompareShop", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeServer(target.DataSource), NormalizeServer(allowedServer), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QC-SL1815 fixtures require the allowlisted dev SQL server and CompareShop database.");

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
        var latest = await LatestManifest(db);
        if (latest.HasValue && latest.Value.Action == ManifestActionSeeded)
            return new { alreadySeeded = true, manifest = latest.Value.Manifest };

        if (await db.DataSources.AnyAsync(x => x.Key.StartsWith(Prefix))
            || await db.Categories.AnyAsync(x => x.Slug.StartsWith(Prefix))
            || await db.PartnerShops.AnyAsync(x => x.Slug.StartsWith(Prefix))
            || await db.Products.AnyAsync(x => x.Slug.StartsWith(Prefix)))
            throw new InvalidOperationException("QC-SL1815 namespace is already occupied without an ownership manifest; refusing to seed.");

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
        var manifest = new FixtureManifest(
            [category.Id], [shopA.Id, shopB.Id], [product.Id], [sourceA.Id, sourceB.Id, sourceNoShop.Id],
            [offerA.Id, offerB.Id], [mappingA.Id, mappingB.Id, mappingNoOffer.Id, mappingLinked.Id],
            [mA.Id, mB.Id, nullable.Id, missingMapping.Id, linked.Id, missingShopMatching.Id, likelyNew.Id]);
        await WriteManifest(db, ManifestActionSeeded, manifest);
        return new { alreadySeeded = false, manifest };
    }

    private static async Task<object> Cleanup(CompareShopDbContext db)
    {
        var latest = await LatestManifest(db);
        if (!latest.HasValue || latest.Value.Action != ManifestActionSeeded)
            return new { alreadyClean = true, manifest = (FixtureManifest?)null };
        var manifest = latest.Value.Manifest;

        // Delete only IDs captured by this command's append-only ownership manifest.
        // Keep audit/history rows, including the manifest events themselves.
        await db.SourceItemMappings.Where(x => manifest.MappingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductMatchings.Where(x => manifest.MatchingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductOffers.Where(x => manifest.OfferIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Products.Where(x => manifest.ProductIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.DataSources.Where(x => manifest.DataSourceIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PartnerShops.Where(x => manifest.ShopIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Categories.Where(x => manifest.CategoryIds.Contains(x.Id)).ExecuteDeleteAsync();
        await WriteManifest(db, ManifestActionCleaned, manifest);
        return new { alreadyClean = false, manifest };
    }

    private static async Task<(string Action, FixtureManifest Manifest)?> LatestManifest(CompareShopDbContext db)
    {
        var row = await db.AdminAuditLogs.Where(x => x.ActorSubject == Owner && x.ResourceType == ManifestResourceType && x.ResourceId == Owner)
            .OrderByDescending(x => x.Id).Select(x => new { x.Action, x.AfterJson }).FirstOrDefaultAsync();
        if (row is null) return null;
        if (row.Action is not (ManifestActionSeeded or ManifestActionCleaned) || string.IsNullOrWhiteSpace(row.AfterJson))
            throw new InvalidOperationException("QC-SL1815 ownership manifest is invalid; refusing fixture operation.");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(row.AfterJson)
            ?? throw new InvalidOperationException("QC-SL1815 ownership manifest is invalid; refusing fixture operation.");
        return (row.Action, manifest);
    }

    private static async Task WriteManifest(CompareShopDbContext db, string action, FixtureManifest manifest)
    {
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            ActorSubject = Owner, Action = action, ResourceType = ManifestResourceType, ResourceId = Owner,
            CreatedAtUtc = DateTime.UtcNow, AfterJson = JsonSerializer.Serialize(manifest)
        });
        await db.SaveChangesAsync();
    }

    private static string NormalizeServer(string server)
    {
        server = server.Trim();
        if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
        server = server.Trim().TrimEnd('/');
        const string defaultSqlPort = ",1433";
        return server.EndsWith(defaultSqlPort, StringComparison.OrdinalIgnoreCase)
            ? server[..^defaultSqlPort.Length]
            : server;
    }

    private sealed record FixtureManifest(int[] CategoryIds, int[] ShopIds, int[] ProductIds, int[] DataSourceIds, int[] OfferIds, int[] MappingIds, int[] MatchingIds);
}
