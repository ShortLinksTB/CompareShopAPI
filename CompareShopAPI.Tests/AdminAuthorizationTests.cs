using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminAuthorizationTests(SqlServerTestFixture fixture)
{
    private async Task<(CompareShopDbContext Db, AdminService Service)> CreateService()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(fixture.ConnectionString).Options;
        var db = new CompareShopDbContext(options);
        await db.Database.MigrateAsync();
        var store = new EfAdminData(db, TimeProvider.System);
        return (db, new AdminService(store));
    }

    private static async Task<int> AddUser(CompareShopDbContext db, string subject, int roleId)
    {
        var user = new User(subject, DateTime.UtcNow); db.Users.Add(user); await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new() { UserId = user.Id, RoleId = roleId }); await db.SaveChangesAsync(); return user.Id;
    }

    [Fact]
    public void AdminDomainTypesEnforceSettingsFlagAndMatchingInvariants()
    {
        var normalizedSettings = CompareShopAPI.Domain.Admin.AdminSetting.ValidateUpdates(
            new Dictionary<string, string> { ["offers_display_count"] = " 8 " });
        Assert.Equal("8", normalizedSettings["offers_display_count"]);
        Assert.Throws<ArgumentException>(() => CompareShopAPI.Domain.Admin.AdminSetting.ValidateUpdates(
            new Dictionary<string, string> { ["unknown"] = "1" }));
        Assert.Throws<ArgumentException>(() => CompareShopAPI.Domain.Admin.AdminSetting.ValidateUpdates(
            new Dictionary<string, string> { ["offers_display_count"] = "51" }));

        var flag = new CompareShopAPI.Domain.Admin.AdminFeatureFlag(" admin-ui ", true, "  enabled  ");
        Assert.Equal("admin-ui", flag.Key);
        Assert.Equal("enabled", flag.Description);
        Assert.Throws<ArgumentException>(() => new CompareShopAPI.Domain.Admin.AdminFeatureFlag("invalid key", true, null));
        Assert.Throws<ArgumentException>(() => new CompareShopAPI.Domain.Admin.AdminFeatureFlag("flag", true, new string('x', 501)));

        var matching = new CompareShopAPI.Domain.Admin.ProductMatching("Unmatched", null, DateTime.UtcNow);
        Assert.True(matching.TryApprove(42));
        Assert.Equal(42, matching.ProductId);
        Assert.True(matching.IsApproved);
        Assert.False(matching.TryApprove(43));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompareShopAPI.Domain.Admin.ProductMatching("Pending", null, DateTime.UtcNow).TryApprove(0));

        var roleAssignment = new CompareShopAPI.Domain.Admin.AdminRoleAssignment("self", ["products.read", "users.manage"]);
        Assert.True(roleAssignment.CanBeReplacedBy("self", ["products.read"]));
        Assert.False(roleAssignment.CanBeReplacedBy("self", ["products.read", "roles.manage"]));
        Assert.True(roleAssignment.CanBeReplacedBy("another-admin", ["products.read", "roles.manage"]));
    }

    [Fact]
    public async Task ConfiguredFirstAdminCanBootstrapThroughProductionRouteOnce()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CompareShopAdminBootstrap_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var subject = $"bootstrap-{Guid.NewGuid():N}";
        var other = $"bootstrap-other-{Guid.NewGuid():N}";
        try
        {
            await using (var db = new CompareShopDbContext(options)) await db.Database.MigrateAsync();
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:CompareShopDb"] = connection,
                ["Admin:BootstrapSuperAdminSubjects:0"] = subject,
                ["Admin:BootstrapSuperAdminSubjects:1"] = other
            };
            await using var app = new ApiFactory(settings: settings, signing: true);
            var config = app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            Assert.Equal(subject, config["Admin:BootstrapSuperAdminSubjects:0"]);
            using var client = app.CreateClient();
            const string clientAdminRole = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: $"not-allowed-{subject}"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/admin/bootstrap/first-admin", null)).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: subject));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/admin/bootstrap/first-admin", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/products")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/admin/bootstrap/first-admin", null)).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(clientAdminRole, subject: other));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/admin/bootstrap/first-admin", null)).StatusCode);

            await using var verify = new CompareShopDbContext(options);
            var admin = await verify.Users.SingleAsync(x => x.KeycloakSubject == subject);
            Assert.Equal(new[] { 1 }, await verify.AdminUserRoles.AsNoTracking().Where(x => x.UserId == admin.Id).Select(x => x.RoleId).ToArrayAsync());
            Assert.Equal(1, await verify.AdminUserRoles.AsNoTracking().CountAsync(x => x.RoleId == 1));
            Assert.Contains(await verify.AdminAuditLogs.AsNoTracking().Where(x => x.ActorSubject == subject).Select(x => x.Action).ToArrayAsync(), x => x == "bootstrap_first_admin");
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options); await cleanup.Database.EnsureDeletedAsync();
        }

    }

    [Fact]
    public async Task OperatorCanManageCatalogButCannotReadAllPriceAlerts()
    {
        var (db, service) = await CreateService(); await using (db)
        {
            var subject = $"operator-{Guid.NewGuid():N}"; await AddUser(db, subject, 2);
            await service.Categories(subject, 1, 20, default);
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.PriceAlerts(subject, 1, 20, null, null, default));
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Users(subject, 1, 20, default));
        }
    }

    [Fact]
    public async Task ViewerCanReadCatalogButCannotMutateIt()
    {
        var (db, service) = await CreateService(); await using (db)
        {
            var subject = $"viewer-{Guid.NewGuid():N}"; await AddUser(db, subject, 4);
            await service.Products(subject, 1, 20, default);
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.CreateProduct(subject, new("x", "X", null, null, 1), default));
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.SaveSettings(subject, new Dictionary<string, string> { ["offers_display_count"] = "5" }, default));
        }
    }

    [Fact]
    public async Task MissingSubjectIsUnauthorizedAndUnknownSubjectFailsClosed()
    {
        var (db, service) = await CreateService(); await using (db)
        {
            await Assert.ThrowsAsync<AdminUnauthorizedException>(() => service.Products(null, 1, 20, default));
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Products("unknown-subject", 1, 20, default));
        }
    }

    [Fact]
    public async Task SuperAdminCannotEscalateTheirOwnRoleAssignments()
    {
        var (db, service) = await CreateService(); await using (db)
        {
            var subject = $"root-{Guid.NewGuid():N}"; var userId = await AddUser(db, subject, 1);
            var elevated = new CompareShopAPI.Domain.Admin.AdminPermission { Key = "future.elevated" };
            db.AdminPermissions.Add(elevated); await db.SaveChangesAsync();
            db.AdminRolePermissions.Add(new() { RoleId = 2, PermissionId = elevated.Id }); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<AdminForbiddenException>(() => service.ReplaceRoles(subject, userId, [2], default));
            Assert.Equal(new[] { 1 }, await db.AdminUserRoles.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.RoleId).ToArrayAsync());
        }
    }

    [Fact]
    public async Task SuperAdminCanAssignRolesToAnotherUserAndAssignmentIsAudited()
    {
        var (db, service) = await CreateService(); await using (db)
        {
            var root = $"root-{Guid.NewGuid():N}"; var target = $"target-{Guid.NewGuid():N}";
            await AddUser(db, root, 1); var userId = await AddUser(db, target, 5);
            var roles = await service.ReplaceRoles(root, userId, [2], default);
            Assert.Equal(new[] { "Operator" }, roles);
            var audit = await db.AdminAuditLogs.AsNoTracking().SingleAsync(x => x.ActorSubject == root && x.ResourceId == userId.ToString());
            Assert.Contains("Customer", audit.BeforeJson); Assert.Contains("Operator", audit.AfterJson);
        }
    }

    [Fact]
    public async Task AdminRoutesEnforceApplicationPermissionsAndArticleSeoIsUnavailable()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CompareShopAdminTest_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                var operatorSub = $"operator-{Guid.NewGuid():N}"; var viewerSub = $"viewer-{Guid.NewGuid():N}";
                var otherSub = $"other-{Guid.NewGuid():N}";
                await AddUser(db, operatorSub, 2); await AddUser(db, viewerSub, 4); await AddUser(db, otherSub, 1);
                var settings = new Dictionary<string, string?> { ["ConnectionStrings:CompareShopDb"] = connection };
                await using var app = new ApiFactory(settings: settings, signing: true);
                using var client = app.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/categories")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", includeSubject: false));
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/me/permissions")).StatusCode);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: operatorSub));
                var ownPermissionsResponse = await client.GetAsync("/admin/me/permissions");
                Assert.Equal(HttpStatusCode.OK, ownPermissionsResponse.StatusCode);
                var ownPermissions = await ReadPermissions(ownPermissionsResponse);
                Assert.NotNull(ownPermissions);
                Assert.Contains("products.read", ownPermissions);
                Assert.Contains("products.create", ownPermissions);
                Assert.Contains("compliance.manage", ownPermissions);
                Assert.DoesNotContain("users.manage", ownPermissions);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: viewerSub));
                var viewerPermissions = await ReadPermissions(await client.GetAsync("/admin/me/permissions"));
                Assert.NotNull(viewerPermissions);
                Assert.Contains("products.read", viewerPermissions);
                Assert.DoesNotContain("products.create", viewerPermissions);
                Assert.DoesNotContain("compliance.manage", viewerPermissions);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: otherSub));
                var otherPermissions = await ReadPermissions(await client.GetAsync("/admin/me/permissions"));
                Assert.NotNull(otherPermissions);
                Assert.Contains("compliance.manage", otherPermissions);
                Assert.Contains("users.manage", otherPermissions);
                Assert.DoesNotContain("users.manage", ownPermissions);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: operatorSub));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/categories")).StatusCode);
                var created = await client.PostAsJsonAsync("/admin/categories", new { slug = $"test-{Guid.NewGuid():N}", name = "Test", isActive = true });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/audit-logs")).StatusCode);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: viewerSub));
                var articleSeo = await client.GetAsync("/admin/seo/article/10");
                Assert.Equal(HttpStatusCode.NotFound, articleSeo.StatusCode);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options); await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SuperAdminCatalogAndManagementOperationsPersistAndAudit()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CompareShopAdminOps_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options); await db.Database.MigrateAsync();
            var subject = $"root-{Guid.NewGuid():N}"; var userId = await AddUser(db, subject, 1);
            var targetSubject = $"customer-{Guid.NewGuid():N}"; var targetUserId = await AddUser(db, targetSubject, 5);
            var service = new AdminService(new EfAdminData(db, TimeProvider.System));

            var category = await service.CreateCategory(subject, new("admin-category", "Admin Category"), default);
            await service.UpdateCategory(subject, category.Id, new("admin-category-updated", "Updated Category", false), default);
            var disposableCategory = await service.CreateCategory(subject, new("delete-category", "Delete Category"), default);
            var shop = await service.CreateShop(subject, new("admin-shop", "Admin Shop", "https://shop.example"), default);
            var product = await service.CreateProduct(subject, new("admin-product", "Admin Product", "Brand", "Description", category.Id), default);
            var offer = new CompareShopAPI.Domain.Catalog.ProductOffer { ProductId = product.Id, PartnerShopId = shop.Id, PriceVnd = 100, Url = "https://shop.example/item", UpdatedAtUtc = DateTime.UtcNow, IsActive = false };
            db.ProductOffers.Add(offer); await db.SaveChangesAsync();
            await service.UpdateProduct(subject, product.Id, new("admin-product", "Updated Product", "Brand", "Updated", category.Id, true), default);
            Assert.NotNull(await service.Product(subject, product.Id, default)); Assert.Single((await service.Products(subject, 1, 20, default)).Items);
            Assert.Contains((await service.Shops(subject, 1, 20, default)).Items, item => item.Slug == shop.Slug); Assert.NotNull(await service.Shop(subject, shop.Id, default));
            Assert.Single(await service.Offers(subject, default));
            await service.UpdateOffer(subject, offer.Id, new(200, "https://shop.example/item?access_token=do-not-log", false, true), default);
            await service.ApproveOffer(subject, offer.Id, default);
            Assert.DoesNotContain("do-not-log", string.Join(" ", await db.AdminAuditLogs.AsNoTracking().Select(x => x.BeforeJson + x.AfterJson).ToArrayAsync()));

            var affiliate = new CompareShopAPI.Domain.Catalog.AffiliateLink { ProductOfferId = offer.Id, DestinationUrl = offer.Url!, IsEnabled = true, IsStale = false, ClickCount = 7, ConversionCount = 2, Revenue = 12.5m };
            db.AffiliateLinks.Add(affiliate);
            var matching = new CompareShopAPI.Domain.Admin.ProductMatching("Unmatched", "Brand", DateTime.UtcNow);
            db.ProductMatchings.Add(matching);
            db.PriceAlerts.Add(new CompareShopAPI.Domain.UserCommerce.PriceAlert(userId, product.Id, 50, DateTime.UtcNow));
            await db.SaveChangesAsync();
            Assert.Single(await service.MatchingList(subject, default)); await service.ApproveMatching(subject, matching.Id, product.Id, default);
            Assert.Equal(1, (await service.PriceAlerts(subject, 1, 20, product.Id, userId, default)).TotalItems);
            Assert.Equal(new AdminAnalytics(7, 2, 12.5m), await service.Analytics(subject, default));
            Assert.Equal(1, (await service.Dashboard(subject, default)).Products);
            Assert.False((await service.Dashboard(subject, default)).ArticlesAvailable);
            await service.SaveSeoProduct(subject, product.Id, new("SEO title", "SEO description"), default);
            Assert.Equal("SEO title", (await service.SeoProduct(subject, product.Id, default))?.SeoTitle);

            Assert.Equal(2, (await service.Users(subject, 1, 20, default)).TotalItems);
            Assert.Equal(new[] { "Operator" }, await service.ReplaceRoles(subject, targetUserId, [2], default));
            await service.SaveFeatureFlag(subject, "admin-ui", new(true, "enabled for tests"), default);
            Assert.True((await service.FeatureFlags(subject, default)).Single().Enabled);
            var settings = await service.SaveSettings(subject, new Dictionary<string, string> { ["price_alert_default_threshold"] = "1000", ["offers_display_count"] = "8" }, default);
            Assert.Equal(2, settings.Count); Assert.Equal(2, (await service.Settings(subject, default)).Count);
            Assert.True((await service.AuditLogs(subject, 1, 100, default)).TotalItems >= 10);

            await service.DeleteProduct(subject, product.Id, default);
            await service.DeleteShop(subject, shop.Id, default);
            await service.DeleteCategory(subject, disposableCategory.Id, default);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options); await cleanup.Database.EnsureDeletedAsync();
        }

    }

    private static async Task<string[]> ReadPermissions(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Array, root.ValueKind);
        Assert.True(root.TryGetProperty("permissions", out var permissions));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, permissions.ValueKind);
        return permissions.EnumerateArray().Select(permission => permission.GetString()!).ToArray();
    }
}
