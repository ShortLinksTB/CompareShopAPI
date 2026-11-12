using System.Data;
using System.Text.Json;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Application.Admin;
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
    private const int CurrentManifestVersion = 3;
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
        await using var transaction = args[0] == "cleanup-matching-fixtures"
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : await db.Database.BeginTransactionAsync();
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
        shops = await db.PartnerShops.CountAsync(), syncJobs = await db.SyncJobs.CountAsync(), groups = await FixtureGroupCount(db),
        fixtureMatchingRows = await db.ProductMatchings.Where(x => x.Name.StartsWith(Owner)).CountAsync()
    };

    private static async Task<int> FixtureGroupCount(CompareShopDbContext db)
    {
        var rows = await db.ProductMatchings.AsNoTracking().Where(x => x.Name.StartsWith(Owner))
            .Select(x => new { x.Name, x.Brand, x.DataSourceId, x.ExternalSku }).ToListAsync();
        var identities = rows.Where(x => x.DataSourceId.HasValue && x.ExternalSku != null).Select(x => new { SourceId = x.DataSourceId!.Value, Sku = x.ExternalSku! }).ToArray();
        var sourceIds = identities.Select(x => x.SourceId).Distinct().ToArray();
        var skus = identities.Select(x => x.Sku).Distinct().ToArray();
        var mappings = await db.SourceItemMappings.AsNoTracking().Where(x => sourceIds.Contains(x.DataSourceId) && skus.Contains(x.ExternalSku))
            .Select(x => new { x.DataSourceId, x.ExternalSku, x.SpecificationsJson }).ToListAsync();
        return rows.Select(row =>
            {
                var mapping = mappings.FirstOrDefault(x => x.DataSourceId == row.DataSourceId && x.ExternalSku == row.ExternalSku);
                var attrs = LaptopMatchingAttributeParser.Parse(row.Name, row.Brand, mapping?.SpecificationsJson);
                return attrs.Brand is not null && attrs.Line is not null && attrs.Chip is not null && attrs.Gpu is not null && attrs.RamGb.HasValue &&
                    attrs.SsdGb.HasValue && attrs.Screen is not null && attrs.IsAccessory == false
                    ? string.Join('|', attrs.Brand.ToLowerInvariant(), attrs.Line.ToLowerInvariant(), attrs.Chip.ToLowerInvariant(), attrs.Gpu.ToLowerInvariant(), attrs.RamGb.Value, attrs.SsdGb.Value, attrs.Screen.ToLowerInvariant())
                    : null;
            }).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private static async Task<object> Seed(CompareShopDbContext db)
    {
        var latest = await LatestManifest(db);
        if (latest.HasValue && latest.Value.Action == ManifestActionSeeded)
        {
            if (latest.Value.Manifest.Version >= CurrentManifestVersion)
                return new { alreadySeeded = true, manifest = latest.Value.Manifest };

            var wasV2Manifest = latest.Value.Manifest.Version >= 2;
            var expanded = latest.Value.Manifest.Version < 2
                ? await ExpandFixtures(db, latest.Value.Manifest)
                : latest.Value.Manifest;
            if (wasV2Manifest && expanded.Version < 3)
                expanded = await ExpandFixturesV3(db, expanded);
            await WriteManifest(db, ManifestActionSeeded, expanded);
            return new { alreadySeeded = false, expanded = true, manifest = expanded };
        }

        if (await db.DataSources.AnyAsync(x => x.Key.StartsWith(Prefix))
            || await db.Categories.AnyAsync(x => x.Slug.StartsWith(Prefix))
            || await db.PartnerShops.AnyAsync(x => x.Slug.StartsWith(Prefix))
            || await db.Products.AnyAsync(x => x.Slug.StartsWith(Prefix)))
            throw new InvalidOperationException("QC-SL1815 namespace is already occupied without an ownership manifest; refusing to seed.");

        // A matching fixture must never create or alter a shared catalog category.
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Level == 2 &&
            (x.Slug.Contains("laptop") || x.Name.Contains("laptop") || db.Categories.Any(parent => parent.Id == x.ParentCategoryId &&
                (parent.Slug.Contains("laptop") || parent.Name.Contains("laptop")))));
        if (category is null) throw new MatchingLaptopCategoryUnavailableException();
        var shopA = new PartnerShop { Slug = Prefix + "shop-a", Name = Owner + " Shop A", WebsiteUrl = "https://shop-a.example.test", IsActive = true };
        var shopB = new PartnerShop { Slug = Prefix + "shop-b", Name = Owner + " Shop B", WebsiteUrl = "https://shop-b.example.test", IsActive = true };
        db.AddRange(shopA, shopB);
        await db.SaveChangesAsync();

        const string fixtureLaptopName = "ASUS VivoBook 15 X1504ZA Core i5-13420H RTX 4050 8GB RAM 512GB SSD 15.6 inch";
        var product = new Product { Slug = Prefix + "existing-product", Name = fixtureLaptopName, Brand = "ASUS", CategoryId = category.Id, IsActive = true };
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
        var mA = ProductMatching.ForSourceItem(Owner + " " + product.Name, "ASUS", FixtureTime, sourceA.Id, skuA);
        var mB = ProductMatching.ForSourceItem(Owner + " " + product.Name, "ASUS", FixtureTime, sourceB.Id, skuB);
        var nullable = new ProductMatching(Owner + " unlinked nullable relation case", null, FixtureTime);
        var missingMapping = ProductMatching.ForSourceItem(Owner + " missing mapping ASUS VivoBook 15 X1504ZA Intel Core i5 8GB RAM 512GB SSD", "ASUS", FixtureTime, sourceA.Id, Owner + "-missing-mapping");
        var linked = ProductMatching.ForSourceItem(Owner + " linked ASUS VivoBook 15 X1504ZA Intel Core i5 8GB RAM 512GB SSD", "ASUS", FixtureTime, sourceA.Id, skuLinked);
        linked.TryApprove(product.Id);
        var missingShopMatching = ProductMatching.ForSourceItem(Owner + " " + product.Name, "ASUS", FixtureTime, sourceNoShop.Id, skuNoShop);
        var likelyNew = new ProductMatching(Owner + " NOVEL LAPTOP AuroraBook Z1 with 16GB RAM and 1TB SSD", "Aurora", FixtureTime);
        db.ProductMatchings.AddRange(mA, mB, nullable, missingMapping, linked, missingShopMatching, likelyNew);
        await db.SaveChangesAsync();
        var manifest = new FixtureManifest(
            [], [shopA.Id, shopB.Id], [product.Id], [sourceA.Id, sourceB.Id, sourceNoShop.Id],
            [offerA.Id, offerB.Id], [mappingA.Id, mappingB.Id, mappingNoOffer.Id, mappingLinked.Id],
            [mA.Id, mB.Id, nullable.Id, missingMapping.Id, linked.Id, missingShopMatching.Id, likelyNew.Id], 1);
        manifest = await ExpandFixtures(db, manifest);
        await WriteManifest(db, ManifestActionSeeded, manifest);
        return new { alreadySeeded = false, manifest };
    }

    private static async Task<FixtureManifest> ExpandFixtures(CompareShopDbContext db, FixtureManifest manifest)
    {
        if (manifest.Version >= 2) return manifest;
        var sourceId = manifest.DataSourceIds.FirstOrDefault();
        if (sourceId <= 0 || !await db.DataSources.AnyAsync(x => x.Id == sourceId))
            throw new InvalidOperationException("QC-SL1815 manifest has no usable data source for additive fixture expansion.");

        var groupName = Owner + " ASUS VivoBook 15 X1504ZA Core i5-13420H RTX 4050 16GB RAM 1TB SSD 15.6 inch";
        var createName = Owner + " ASUS VivoBook 15 X1504ZA Core i5-13420H RTX 4050 8GB RAM 512GB SSD 15.6 inch";
        var groupSkus = new[] { Owner + "-v2-group-a", Owner + "-v2-group-b" };
        var createSku = Owner + "-v2-create-product";
        var skus = groupSkus.Append(createSku).ToArray();
        if (await db.SourceItemMappings.AnyAsync(x => x.DataSourceId == sourceId && skus.Contains(x.ExternalSku))
            || await db.ProductMatchings.AnyAsync(x => x.DataSourceId == sourceId && skus.Contains(x.ExternalSku)))
            throw new InvalidOperationException("QC-SL1815 expansion namespace is occupied by rows absent from its ownership manifest.");

        var mappings = new List<SourceItemMapping>();
        foreach (var sku in skus)
        {
            var isCreateScenario = sku == createSku;
            var fixtureName = isCreateScenario ? createName : groupName;
            var ram = isCreateScenario ? "8GB" : "16GB";
            var ssd = isCreateScenario ? "512GB" : "1TB";
            var mapping = new SourceItemMapping(sourceId, sku, fixtureName, "ASUS");
            mapping.UpdateSnapshot(fixtureName, "ASUS", new ScrapedOffer(sku, fixtureName, "ASUS", 15990000,
                "https://shop-a.example.test/" + sku, null, null,
                new Dictionary<string, string> { ["ram"] = ram, ["ssd"] = ssd }, "in_stock"));
            mappings.Add(mapping);
        }
        db.SourceItemMappings.AddRange(mappings);
        await db.SaveChangesAsync();

        var matchings = new List<ProductMatching>();
        foreach (var sku in skus)
        {
            var matchingName = sku == createSku ? createName : groupName;
            matchings.Add(ProductMatching.ForSourceItem(matchingName, "ASUS", FixtureTime, sourceId, sku));
        }
        db.ProductMatchings.AddRange(matchings);
        await db.SaveChangesAsync();

        return manifest with
        {
            MappingIds = manifest.MappingIds.Concat(mappings.Select(x => x.Id)).ToArray(),
            MatchingIds = manifest.MatchingIds.Concat(matchings.Select(x => x.Id)).ToArray(),
            Version = 2
        };
    }

    private static async Task<FixtureManifest> ExpandFixturesV3(CompareShopDbContext db, FixtureManifest manifest)
    {
        // Laptop 4008 is shared catalog data. Validate it before creating any v3-owned rows.
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.Id == 4008 && x.Level == 2 &&
            (x.Slug.Contains("laptop") || x.Name.Contains("laptop"))))
            throw new InvalidOperationException("QC-SL1815 v3 requires existing Laptop category 4008; no v3 fixture writes were made.");

        const string v3Owner = Owner + "-v3";
        const string sku = Owner + "-v3-create-product";
        if (await db.DataSources.AnyAsync(x => x.Key == v3Owner + "-source")
            || await db.PartnerShops.AnyAsync(x => x.Slug == v3Owner + "-shop")
            || await db.SourceItemMappings.AnyAsync(x => x.ExternalSku == sku)
            || await db.ProductMatchings.AnyAsync(x => x.ExternalSku == sku))
            throw new InvalidOperationException("QC-SL1815 v3 namespace is occupied by records absent from its ownership manifest; refusing to seed.");

        var shop = new PartnerShop { Slug = v3Owner + "-shop", Name = v3Owner + " Shop", WebsiteUrl = "https://v3-shop.example.test", IsActive = true };
        db.PartnerShops.Add(shop);
        await db.SaveChangesAsync();
        var source = new DataSource(Prefix + "v3-source", v3Owner + " Shop", "/qc-sl1815/laptop", shop.Id, FixtureTime);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        const string name = "QC-SL1815 ASUS VivoBook 15 X1504ZA Core i5-13420H RTX 4050 8GB RAM 512GB SSD 15.6 inch";
        var catalogProduct = new Product { Slug = v3Owner + "-existing-product", Name = name, Brand = "ASUS", CategoryId = 4008, IsActive = true };
        db.Products.Add(catalogProduct);
        await db.SaveChangesAsync();
        var offer = new ProductOffer { ProductId = catalogProduct.Id, PartnerShopId = shop.Id, PriceVnd = 15990000, OriginalPriceVnd = 16990000,
            Url = "https://v3-shop.example.test/qc-sl1815-v3", ImageUrl = "https://v3-shop.example.test/qc-sl1815-v3.jpg",
            SpecificationsJson = "{\"ram\":\"8GB\",\"ssd\":\"512GB\"}", StockStatus = "in_stock", UpdatedAtUtc = FixtureTime, IsActive = true };
        db.ProductOffers.Add(offer);
        await db.SaveChangesAsync();
        var mapping = new SourceItemMapping(source.Id, sku, name, "ASUS");
        mapping.SetOffer(offer.Id);
        mapping.UpdateSnapshot(name, "ASUS", new ScrapedOffer(sku, name, "ASUS", offer.PriceVnd, offer.Url!, offer.ImageUrl,
            offer.OriginalPriceVnd, new Dictionary<string, string> { ["ram"] = "8GB", ["ssd"] = "512GB" }, "in_stock"));
        var matching = ProductMatching.ForSourceItem(name, "ASUS", FixtureTime, source.Id, sku);
        db.SourceItemMappings.Add(mapping);
        db.ProductMatchings.Add(matching);
        await db.SaveChangesAsync();

        return manifest with
        {
            ShopIds = manifest.ShopIds.Append(shop.Id).ToArray(),
            DataSourceIds = manifest.DataSourceIds.Append(source.Id).ToArray(),
            OfferIds = manifest.OfferIds.Append(offer.Id).ToArray(),
            ProductIds = manifest.ProductIds.Append(catalogProduct.Id).ToArray(),
            MappingIds = manifest.MappingIds.Append(mapping.Id).ToArray(),
            MatchingIds = manifest.MatchingIds.Append(matching.Id).ToArray(),
            Version = 3,
            V3ShopIds = [shop.Id], V3DataSourceIds = [source.Id], V3OfferIds = [offer.Id],
            V3MappingIds = [mapping.Id], V3MatchingIds = [matching.Id], V3ProductIds = [catalogProduct.Id]
        };
    }

    private static async Task<object> Cleanup(CompareShopDbContext db)
    {
        var latest = await LatestManifest(db);
        if (!latest.HasValue || latest.Value.Action != ManifestActionSeeded)
            return new { alreadyClean = true, manifest = (FixtureManifest?)null };
        var manifest = latest.Value.Manifest;
        var cleanupManifest = manifest;
        // v3 cleanup owns only its isolated append; the v2 snapshot remains usable by QC.
        if (manifest.Version >= 3 && manifest.V3MatchingIds is not null)
            cleanupManifest = manifest with
            {
                CategoryIds = [], ProductIds = manifest.V3ProductIds ?? [],
                ShopIds = manifest.V3ShopIds ?? [], DataSourceIds = manifest.V3DataSourceIds ?? [],
                OfferIds = manifest.V3OfferIds ?? [], MappingIds = manifest.V3MappingIds ?? [], MatchingIds = manifest.V3MatchingIds
            };
        // Products created later through the matching endpoint are owned only when its audit event
        // names one of the exact matching IDs in this command's append-only manifest.
        var creationAudits = await db.AdminAuditLogs.AsNoTracking()
            .Where(x => x.Action == "create_from_matching" && x.ResourceType == "product" && x.AfterJson != null)
            .Select(x => x.AfterJson).ToListAsync();
        var matchingIds = cleanupManifest.MatchingIds.ToHashSet();
        var createdProductIds = creationAudits.Select(ParseCreatedProduct)
            .Where(x => x.HasValue && x.Value.MatchingIds.Any(matchingIds.Contains))
            .Select(x => x!.Value.ProductId);
        var syncJobs = await DiscoverFixtureSyncJobs(db, cleanupManifest);
        var recordedSyncJobIds = cleanupManifest.SyncJobIds ?? [];
        if (recordedSyncJobIds.Any(x => x <= 0) || recordedSyncJobIds.Distinct().Count() != recordedSyncJobIds.Length ||
            (cleanupManifest.SyncJobIds is not null && !recordedSyncJobIds.ToHashSet().SetEquals(syncJobs.Select(x => x.Id))))
            throw new InvalidOperationException("QC-SL1815 SyncJob manifest does not match the complete fixture-source retry-chain closure; refusing cleanup.");
        var discoveredJobIds = syncJobs.Select(x => x.Id).ToArray();
        var beforeNonFixtureJobIds = await db.SyncJobs.AsNoTracking()
            .Where(x => !discoveredJobIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync();

        var unmanifestedMappings = await db.SourceItemMappings.AsNoTracking()
            .Where(x => cleanupManifest.DataSourceIds.Contains(x.DataSourceId) && !cleanupManifest.MappingIds.Contains(x.Id))
            .Select(x => x.Id).ToArrayAsync();
        if (unmanifestedMappings.Length > 0)
            throw new InvalidOperationException("QC-SL1815 DataSources have mappings outside the exact ownership manifest; refusing cleanup.");

        var syncJobIds = syncJobs.Select(x => x.Id).Order().ToArray();
        cleanupManifest = cleanupManifest with
        {
            ProductIds = cleanupManifest.ProductIds.Concat(createdProductIds).Distinct().ToArray(),
            SyncJobIds = syncJobIds
        };

        // Persist the verified exact job closure to append-only manifest history in the same
        // serializable transaction before deleting any owned row. Any later failure rolls this back.
        if (manifest.Version >= 3)
            manifest = manifest with { V3ProductIds = cleanupManifest.ProductIds };
        else
            manifest = cleanupManifest;
        await WriteManifest(db, ManifestActionSeeded, manifest);

        // Delete only IDs captured by this command's append-only ownership manifest.
        // Keep audit/history rows, including the manifest events themselves.
        foreach (var syncJobId in SyncJobDeletionOrder(syncJobs))
            await db.SyncJobs.Where(x => x.Id == syncJobId && syncJobIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.SourceItemMappings.Where(x => cleanupManifest.MappingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductMatchings.Where(x => cleanupManifest.MatchingIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProductOffers.Where(x => cleanupManifest.OfferIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Products.Where(x => cleanupManifest.ProductIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.DataSources.Where(x => cleanupManifest.DataSourceIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PartnerShops.Where(x => cleanupManifest.ShopIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Categories.Where(x => cleanupManifest.CategoryIds.Contains(x.Id)).ExecuteDeleteAsync();

        var remainingJobs = await db.SyncJobs.AsNoTracking()
            .Where(x => syncJobIds.Contains(x.Id) || cleanupManifest.DataSourceIds.Contains(x.DataSourceId))
            .Select(x => x.Id).ToArrayAsync();
        if (remainingJobs.Length > 0)
            throw new InvalidOperationException("QC-SL1815 cleanup left SyncJobs related to manifest-owned DataSources; refusing to commit.");
        var afterNonFixtureJobIds = await db.SyncJobs.AsNoTracking()
            .Where(x => !syncJobIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync();
        if (!beforeNonFixtureJobIds.ToHashSet().SetEquals(afterNonFixtureJobIds))
            throw new InvalidOperationException("QC-SL1815 cleanup changed SyncJobs outside the exact fixture ownership manifest; refusing to commit.");
        var remainingSources = await db.DataSources.AsNoTracking().Where(x => cleanupManifest.DataSourceIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync();
        if (remainingSources.Length > 0)
            throw new InvalidOperationException("QC-SL1815 cleanup left manifest-owned DataSources; refusing to commit.");
        await WriteManifest(db, ManifestActionCleaned, manifest);
        return new { alreadyClean = false, manifest };
    }

    private static async Task<IReadOnlyList<SyncJob>> DiscoverFixtureSyncJobs(CompareShopDbContext db, FixtureManifest manifest)
    {
        var sourceIds = manifest.DataSourceIds.Distinct().ToArray();
        if (sourceIds.Length != manifest.DataSourceIds.Length || sourceIds.Any(x => x <= 0))
            throw new InvalidOperationException("QC-SL1815 DataSource manifest is invalid; refusing cleanup.");

        var jobs = new Dictionary<int, SyncJob>();
        var directJobs = await db.SyncJobs.AsNoTracking().Where(x => sourceIds.Contains(x.DataSourceId)).ToListAsync();
        foreach (var job in directJobs) AddInactiveFixtureJob(jobs, job, sourceIds);

        var inspectedParents = new HashSet<int>();
        while (true)
        {
            var pendingParents = jobs.Values.Where(x => x.RetryOfJobId.HasValue)
                .Select(x => x.RetryOfJobId!.Value).Where(x => !inspectedParents.Contains(x)).Distinct().ToArray();
            if (pendingParents.Length == 0) break;
            var parents = await db.SyncJobs.AsNoTracking().Where(x => pendingParents.Contains(x.Id)).ToListAsync();
            if (parents.Count != pendingParents.Length)
                throw new InvalidOperationException("QC-SL1815 retry chain references a missing SyncJob; refusing cleanup.");
            foreach (var parent in parents) AddInactiveFixtureJob(jobs, parent, sourceIds);
            inspectedParents.UnionWith(pendingParents);
        }

        var inspectedChildren = new HashSet<int>();
        while (true)
        {
            var candidateParents = jobs.Keys.Where(x => !inspectedChildren.Contains(x)).ToArray();
            if (candidateParents.Length == 0) break;
            var children = await db.SyncJobs.AsNoTracking().Where(x => x.RetryOfJobId.HasValue && candidateParents.Contains(x.RetryOfJobId.Value)).ToListAsync();
            foreach (var child in children) AddInactiveFixtureJob(jobs, child, sourceIds);
            inspectedChildren.UnionWith(candidateParents);
        }

        var exactIds = jobs.Keys.ToHashSet();
        if (jobs.Values.Any(x => x.RetryOfJobId.HasValue && !exactIds.Contains(x.RetryOfJobId.Value)))
            throw new InvalidOperationException("QC-SL1815 retry chain is not fully contained in the exact fixture ownership closure; refusing cleanup.");
        return jobs.Values.ToArray();
    }

    private static void AddInactiveFixtureJob(IDictionary<int, SyncJob> jobs, SyncJob job, int[] sourceIds)
    {
        if (!sourceIds.Contains(job.DataSourceId))
            throw new InvalidOperationException("QC-SL1815 retry chain crosses into a non-fixture DataSource; refusing cleanup.");
        if (job.State is not (SyncJobState.Success or SyncJobState.Partial or SyncJobState.Failed))
            throw new InvalidOperationException("QC-SL1815 fixture DataSource has an active or non-terminal SyncJob; refusing cleanup before writes.");
        jobs[job.Id] = job;
    }

    private static IReadOnlyList<int> SyncJobDeletionOrder(IReadOnlyList<SyncJob> jobs)
    {
        var byId = jobs.ToDictionary(x => x.Id);
        var remainingChildren = jobs.ToDictionary(x => x.Id, _ => 0);
        foreach (var job in jobs)
        {
            if (job.RetryOfJobId is int parentId)
            {
                if (!byId.ContainsKey(parentId))
                    throw new InvalidOperationException("QC-SL1815 retry chain is not fully contained in its ownership manifest; refusing cleanup.");
                remainingChildren[parentId]++;
            }
        }

        var leaves = new SortedSet<int>(remainingChildren.Where(x => x.Value == 0).Select(x => x.Key));
        var ordered = new List<int>(jobs.Count);
        while (leaves.Count > 0)
        {
            var leafId = leaves.Min;
            leaves.Remove(leafId);
            ordered.Add(leafId);
            if (byId[leafId].RetryOfJobId is int parentId && --remainingChildren[parentId] == 0)
                leaves.Add(parentId);
        }
        if (ordered.Count != jobs.Count)
            throw new InvalidOperationException("QC-SL1815 retry chain contains a cycle; refusing cleanup.");
        return ordered;
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

    private static (int ProductId, int[] MatchingIds)? ParseCreatedProduct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("productId", out var productId) || !productId.TryGetInt32(out var id) || id <= 0 ||
                !root.TryGetProperty("matchingIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
            return (id, ids.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToArray());
        }
        catch (JsonException) { return null; }
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

    private sealed record FixtureManifest(int[] CategoryIds, int[] ShopIds, int[] ProductIds, int[] DataSourceIds, int[] OfferIds, int[] MappingIds, int[] MatchingIds, int Version = 1, int[]? SyncJobIds = null,
        int[]? V3ShopIds = null, int[]? V3DataSourceIds = null, int[]? V3OfferIds = null, int[]? V3MappingIds = null, int[]? V3MatchingIds = null, int[]? V3ProductIds = null);
}
