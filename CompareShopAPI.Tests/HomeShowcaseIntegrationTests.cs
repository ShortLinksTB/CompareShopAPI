using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Application.HomeShowcase;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.HomeShowcase;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.HomeShowcase;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class HomeShowcaseIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Migration_store_permissions_publish_visibility_and_preview_token_follow_the_contract()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopHomeShowcase_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var clock = new FixedHomeClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await MigrationTestHelpers.MigrateToLatestAsync(db);
                await MigrationTestHelpers.MigrateToLatestAsync(db);
                foreach (var key in new[] { AdminPermissions.HomeShowcaseRead, AdminPermissions.HomeShowcaseUpdate, AdminPermissions.HomeShowcasePublish })
                {
                    var permissionId = await db.AdminPermissions.Where(permission => permission.Key == key).Select(permission => permission.Id).SingleAsync();
                    Assert.Contains(1, await db.AdminRolePermissions.Where(grant => grant.PermissionId == permissionId).Select(grant => grant.RoleId).ToListAsync());
                    Assert.Contains(2, await db.AdminRolePermissions.Where(grant => grant.PermissionId == permissionId).Select(grant => grant.RoleId).ToListAsync());
                }

                var category = new Category { Slug = "computers", Name = "Computers", Level = 1, IsActive = true, CountryId = CountryTestCompatibility.VnCountryId };
                db.Categories.Add(category);
                await db.SaveChangesAsync();
                var active = new Product { Slug = "showcase-active", Name = "A Active", CategoryId = category.Id, IsActive = true };
                var hidden = new Product { Slug = "showcase-hidden", Name = "B Hidden", CategoryId = category.Id, IsActive = false };
                var shop = new PartnerShop { Slug = "showcase-shop", Name = "Showcase Shop", CountryId = CountryTestCompatibility.VnCountryId, IsActive = true };
                db.AddRange(active, hidden, shop);
                var user = new User("home-showcase-admin", clock.GetUtcNow().UtcDateTime);
                db.Users.Add(user);
                await db.SaveChangesAsync();
                db.ProductOffers.Add(new ProductOffer { ProductId = active.Id, PartnerShopId = shop.Id, PriceVnd = 1000, UpdatedAtUtc = clock.GetUtcNow().UtcDateTime, IsActive = true });
                await db.SaveChangesAsync();
                db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = 2 });
                await db.SaveChangesAsync();

                var catalog = new EfCatalogQueries(db, clock);
                var store = new EfHomeShowcaseStore(db, catalog, clock);
                var audit = new EfHomeShowcaseAudit(db, clock);
                var service = new HomeShowcaseService(store, audit, new EfAdminData(db, clock), clock);
                await Assert.ThrowsAsync<AdminForbiddenException>(() => service.GetAdmin("unknown-admin", default));

                await store.ReplaceSnapshot(HomeShowcaseSnapshot.Draft, [active.Id, hidden.Id, .. new int?[13]], [active.Id, .. new int?[14]], default);
                var beforePublish = await service.Public(default);
                Assert.Equal("showcase-active", Assert.Single(beforePublish.Featured).Slug);

                await Assert.ThrowsAsync<InvalidOperationException>(() => audit.Execute<bool>("home-showcase-admin", "failed_publish", null, null, async ct =>
                {
                    await store.ReplaceSnapshot(HomeShowcaseSnapshot.Published, [active.Id, .. new int?[14]], new int?[15], ct);
                    throw new InvalidOperationException("force publish rollback");
                }, default));
                db.ChangeTracker.Clear();
                Assert.Empty(await store.Slots(HomeShowcaseSnapshot.Published, default));

                var cacheInvalidated = false;
                await service.Publish("home-showcase-admin", () => cacheInvalidated = true, default);
                Assert.True(cacheInvalidated);
                var published = await service.Public(default);
                Assert.Equal("showcase-active", Assert.Single(published.Featured).Slug);
                Assert.Equal("showcase-active", Assert.Single(published.InterestedComputers).Slug);
                Assert.False(System.Text.Json.JsonSerializer.Serialize(published).Contains("IsVisible", StringComparison.Ordinal));

                await store.ReplaceSnapshot(HomeShowcaseSnapshot.Draft, [hidden.Id, .. new int?[14]], [hidden.Id, .. new int?[14]], default);
                var reset = await service.ResetDraft("home-showcase-admin", default);
                Assert.Equal(active.Id, reset.Draft.Featured[0].Product!.Id);

                var token = await service.CreatePreviewToken("home-showcase-admin", default);
                Assert.NotEqual(token, await db.HomeShowcasePreviewTokens.Select(item => item.TokenHash).SingleAsync());
                Assert.NotNull(await service.Preview(token, default));
                Assert.Null(await service.Preview(token, default));
                var expiringToken = await service.CreatePreviewToken("home-showcase-admin", default);
                clock.Advance(TimeSpan.FromMinutes(11));
                Assert.Null(await service.Preview(expiringToken, default));
                Assert.True(await db.AdminAuditLogs.AnyAsync(entry => entry.Action == "publish" && entry.ResourceType == "home_showcase" && entry.AfterJson!.Contains("changedSlotCount")));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class FixedHomeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now = Now.Add(duration);
    }
}
