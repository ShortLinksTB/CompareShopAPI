using System.Net;
using System.Net.Http.Headers;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminProductDeleteIntegrationTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Delete_returns_safe_conflict_for_restrictive_reference_and_preserves_cascade_deletion()
    {
        var subject = $"product-delete-{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CompareShopProductDelete_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var blockedProductId = 0;
        var cascadeProductId = 0;
        var cascadeOfferId = 0;

        try
        {
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var category = new Category { Slug = "delete-test-category", Name = "Delete Test" };
                var shop = new PartnerShop { Slug = "delete-test-shop", Name = "Delete Test Shop" };
                setup.AddRange(category, shop);
                await setup.SaveChangesAsync();

                var blocked = new Product { Slug = "delete-blocked", Name = "Blocked", CategoryId = category.Id };
                var cascade = new Product { Slug = "delete-cascade", Name = "Cascade", CategoryId = category.Id };
                setup.Products.AddRange(blocked, cascade);
                await setup.SaveChangesAsync();
                blockedProductId = blocked.Id;
                cascadeProductId = cascade.Id;

                var offer = new ProductOffer
                {
                    ProductId = cascade.Id,
                    PartnerShopId = shop.Id,
                    PriceVnd = 100,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                setup.ProductOffers.Add(offer);
                setup.BannedItems.Add(BannedItem.ForProduct(blocked.Id, "integration test", subject, DateTime.UtcNow));
                var user = new User(subject, DateTime.UtcNow);
                setup.Users.Add(user);
                await setup.SaveChangesAsync();
                cascadeOfferId = offer.Id;
                setup.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = 1 });
                await setup.SaveChangesAsync();
            }

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));

            using var conflict = await client.DeleteAsync($"/admin/products/{blockedProductId}");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var conflictBody = await conflict.Content.ReadAsStringAsync();
            Assert.Contains("linked records", conflictBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SqlException", conflictBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FOREIGN KEY", conflictBody, StringComparison.OrdinalIgnoreCase);

            using var deleted = await client.DeleteAsync($"/admin/products/{cascadeProductId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            await using var verify = new CompareShopDbContext(options);
            Assert.True(await verify.Products.AnyAsync(product => product.Id == blockedProductId));
            Assert.False(await verify.Products.AnyAsync(product => product.Id == cascadeProductId));
            Assert.False(await verify.ProductOffers.AnyAsync(offer => offer.Id == cascadeOfferId));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
