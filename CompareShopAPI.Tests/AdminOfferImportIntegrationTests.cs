using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminOfferImportIntegrationTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Create_update_conflict_and_authorization_follow_import_contract()
    {
        var seeded = await Seed("CompareShopOfferImport");
        try
        {
            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = seeded.Connection }, signing: true);
            using var client = AuthorizedClient(app, seeded.WriterSubject);
            var first = await client.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "source-1"));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var offer = await first.Content.ReadFromJsonAsync<ImportedOfferDto>();
            Assert.NotNull(offer);
            Assert.Equal(DateTimeKind.Utc, offer.SourceScrapedAtUtc.Kind);
            Assert.Equal(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc), offer.SourceScrapedAtUtc);

            var updateInput = Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "source-1", price: 2_100_000);
            var second = await client.PostAsJsonAsync("/admin/offers", updateInput);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var updated = await second.Content.ReadFromJsonAsync<ImportedOfferDto>();
            Assert.Equal(offer.Id, updated?.Id);
            Assert.Equal(2_100_000, updated?.PriceVnd);

            using var deniedClient = AuthorizedClient(app, seeded.NoPermissionSubject);
            var forbidden = await deniedClient.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "denied"));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var anonymousClient = app.CreateClient();
            var unauthorized = await anonymousClient.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "anonymous"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using var invalid = await client.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "invalid", price: 0));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var missing = await client.PostAsJsonAsync("/admin/offers", Input(int.MaxValue, seeded.ShopId, seeded.SourceId, "missing"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var conflict = await client.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "conflict"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            await using var verify = new CompareShopDbContext(seeded.Options);
            var persisted = await verify.ProductOffers.SingleAsync(x => x.Id == offer.Id);
            Assert.Equal(2_100_000, persisted.PriceVnd);
            Assert.Equal(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc), persisted.UpdatedAtUtc);
            var mapping = await verify.SourceItemMappings.SingleAsync(x => x.ExternalSku == "source-1");
            Assert.Equal(seeded.ProductId, mapping.ProductId);
            Assert.Equal(offer.Id, mapping.ProductOfferId);
            Assert.Equal(1, await verify.SourceItemMappings.CountAsync(x => x.ExternalSku == "conflict"));
            Assert.Equal(seeded.OtherProductId, (await verify.SourceItemMappings.SingleAsync(x => x.ExternalSku == "conflict")).ProductId);
            var audit = await verify.AdminAuditLogs.Where(x => x.ResourceType == "offer_import" && x.ResourceId == offer.Id.ToString()).ToListAsync();
            Assert.Equal(2, audit.Count);
            Assert.Contains(audit, x => x.Action == "create" && x.AfterJson!.Contains("source-1", StringComparison.Ordinal));
            Assert.Contains(audit, x => x.Action == "update" && x.BeforeJson is not null && x.AfterJson is not null);
            var permission = await verify.AdminPermissions.SingleAsync(x => x.Key == "offers.create");
            var grantedRoleIds = await verify.AdminRolePermissions
                .Where(x => x.PermissionId == permission.Id)
                .Select(x => x.RoleId)
                .OrderBy(x => x)
                .ToListAsync();
            Assert.Equal(new[] { 2, seeded.WriterRoleId }.OrderBy(x => x), grantedRoleIds);
        }
        finally { await Cleanup(seeded); }
    }

    [Fact]
    public async Task Audit_failure_rolls_back_offer_and_mapping_together()
    {
        var seeded = await Seed("CompareShopOfferRollback");
        try
        {
            await using (var setup = new CompareShopDbContext(seeded.Options))
                await setup.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.AdminAuditLogs ADD CONSTRAINT CK_AdminAuditLogs_FailOfferImport CHECK ([ResourceType] <> N'offer_import')");
            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = seeded.Connection }, signing: true);
            using var client = AuthorizedClient(app, seeded.WriterSubject);
            await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsJsonAsync("/admin/offers", Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "rollback")));
            await using var verify = new CompareShopDbContext(seeded.Options);
            Assert.False(await verify.ProductOffers.AnyAsync());
            Assert.False(await verify.SourceItemMappings.AnyAsync(x => x.ExternalSku == "rollback"));
            Assert.False(await verify.AdminAuditLogs.AnyAsync(x => x.ResourceType == "offer_import"));
        }
        finally { await Cleanup(seeded); }
    }

    [Fact]
    public async Task Concurrent_same_identity_imports_create_one_offer_and_mapping()
    {
        var seeded = await Seed("CompareShopOfferRace");
        try
        {
            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = seeded.Connection }, signing: true);
            using var client = AuthorizedClient(app, seeded.WriterSubject);
            var body = Input(seeded.ProductId, seeded.ShopId, seeded.SourceId, "race");
            var results = await Task.WhenAll(client.PostAsJsonAsync("/admin/offers", body), client.PostAsJsonAsync("/admin/offers", body));
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.OK }, results.Select(x => x.StatusCode).OrderBy(x => x == HttpStatusCode.Created ? 0 : 1));
            var ids = await Task.WhenAll(results.Select(async response => (await response.Content.ReadFromJsonAsync<ImportedOfferDto>())!.Id));
            Assert.Equal(ids[0], ids[1]);
            await using var verify = new CompareShopDbContext(seeded.Options);
            Assert.Equal(1, await verify.ProductOffers.CountAsync());
            Assert.Equal(1, await verify.SourceItemMappings.CountAsync(x => x.ExternalSku == "race"));
            Assert.Equal(2, await verify.AdminAuditLogs.CountAsync(x => x.ResourceType == "offer_import"));
        }
        finally { await Cleanup(seeded); }
    }

    private async Task<Seeded> Seed(string prefix)
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"{prefix}_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var writerSubject = $"writer-{Guid.NewGuid():N}";
        var noPermissionSubject = $"viewer-{Guid.NewGuid():N}";
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            var category = await CategoryTestData.AddLeafAsync(db, $"category-{Guid.NewGuid():N}", "Test");
            var shop = await db.PartnerShops.SingleAsync(x => x.Slug == "cellphones");
            await db.SaveChangesAsync();
            var source = await db.DataSources.SingleAsync(x => x.Key == DataSource.CellphoneSKey);
            Assert.Equal(shop.Id, source.PartnerShopId);
            var product = new Product { Slug = $"product-{Guid.NewGuid():N}", Name = "MacBook Air", Brand = "Apple", CategoryId = category.Id };
            var other = new Product { Slug = $"product-{Guid.NewGuid():N}", Name = "Other", Brand = "Apple", CategoryId = category.Id };
            db.Products.AddRange(product, other);
            await db.SaveChangesAsync();

            var permission = await db.AdminPermissions.SingleAsync(x => x.Key == "offers.create");
            var role = new AdminRole { Name = $"Offer Writer {Guid.NewGuid():N}" };
            var writer = new User(writerSubject, DateTime.UtcNow);
            var viewer = new User(noPermissionSubject, DateTime.UtcNow);
            db.AddRange(role, writer, viewer);
            await db.SaveChangesAsync();
            db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
            db.AdminUserRoles.Add(new AdminUserRole { RoleId = role.Id, UserId = writer.Id });
            db.AdminUserRoles.Add(new AdminUserRole { RoleId = 4, UserId = viewer.Id });
            var conflictMapping = new SourceItemMapping(source.Id, "conflict", other.Name, other.Brand);
            db.SourceItemMappings.Add(conflictMapping);
            conflictMapping.Approve(other.Id);
            await db.SaveChangesAsync();
            return new(connection, options, writerSubject, noPermissionSubject, role.Id, product.Id, other.Id, shop.Id, source.Id);
        }
        catch
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
            throw;
        }
    }

    private static HttpClient AuthorizedClient(ApiFactory app, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));
        return client;
    }

    private static OfferImportInput Input(int productId, int shopId, int sourceId, string sku, int price = 1_990_000) =>
        new(productId, shopId, price, 2_190_000, "https://cellphones.com.vn/macbook-air.html", new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc), "https://cellphones.com.vn/images/macbook.jpg", sourceId, sku);

    private static async Task Cleanup(Seeded seeded)
    {
        await using var cleanup = new CompareShopDbContext(seeded.Options);
        await cleanup.Database.EnsureDeletedAsync();
    }

    private sealed record Seeded(string Connection, DbContextOptions<CompareShopDbContext> Options, string WriterSubject, string NoPermissionSubject, int WriterRoleId, int ProductId, int OtherProductId, int ShopId, int SourceId);
    private sealed record ImportedOfferDto(int Id, int ProductId, int PartnerShopId, int PriceVnd, int? OriginalPriceVnd, string SourceUrl, DateTime SourceScrapedAtUtc, string? ImageUrl);
}
