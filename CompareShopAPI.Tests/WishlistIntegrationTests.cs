using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.UserCommerce;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class WishlistIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Wishlist_projects_catalog_images_and_preserves_visibility_and_ordering()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopWishlist_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            int userId;
            Product withImage;
            Product withoutImage;
            Product older;
            var newestAt = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
            var olderAt = newestAt.AddMinutes(-1);
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var category = await CategoryTestData.AddLeafAsync(setup, "wishlist-test-category", "Wishlist Test");

                withImage = new Product
                {
                    Slug = "wishlist-with-image", Name = "With Image", ImageUrl = "https://images.example.test/wishlist.jpg", CategoryId = category.Id
                };
                withoutImage = new Product { Slug = "wishlist-without-image", Name = "Without Image", CategoryId = category.Id };
                older = new Product { Slug = "wishlist-older", Name = "Older", CategoryId = category.Id };
                var inactive = new Product { Slug = "wishlist-inactive", Name = "Inactive", IsActive = false, CategoryId = category.Id };
                var productBanned = new Product { Slug = "wishlist-product-banned", Name = "Product Banned", CategoryId = category.Id };
                var brandBanned = new Product { Slug = "wishlist-brand-banned", Name = "Brand Banned", Brand = "Blocked Brand", CategoryId = category.Id };
                setup.Products.AddRange(withImage, withoutImage, older, inactive, productBanned, brandBanned);
                await setup.SaveChangesAsync();

                var user = new User($"wishlist-{Guid.NewGuid():N}", DateTime.UtcNow);
                setup.Users.Add(user);
                setup.BannedItems.AddRange(
                    BannedItem.ForProduct(productBanned.Id, "test", "wishlist-test", DateTime.UtcNow),
                    BannedItem.ForBrand("Blocked Brand", "test", "wishlist-test", DateTime.UtcNow));
                await setup.SaveChangesAsync();
                userId = user.Id;
                setup.WishlistItems.AddRange(
                    new WishlistItem(userId, withImage.Id, newestAt),
                    new WishlistItem(userId, withoutImage.Id, newestAt),
                    new WishlistItem(userId, older.Id, olderAt),
                    new WishlistItem(userId, inactive.Id, newestAt),
                    new WishlistItem(userId, productBanned.Id, newestAt),
                    new WishlistItem(userId, brandBanned.Id, newestAt));
                await setup.SaveChangesAsync();
            }

            await using var db = new CompareShopDbContext(options);
            var result = await new EfUserCommerceRepository(db).Wishlist(userId, default);

            Assert.Equal(new[] { withImage.Id, withoutImage.Id, older.Id }, result.Select(item => item.ProductId));
            Assert.Equal("https://images.example.test/wishlist.jpg", result[0].ImageUrl);
            Assert.Equal("wishlist-with-image", result[0].Slug);
            Assert.Equal("With Image", result[0].Name);
            Assert.Null(result[1].ImageUrl);
            Assert.Equal("wishlist-without-image", result[1].Slug);
            Assert.Equal(newestAt, result[0].CreatedAtUtc);
            Assert.Equal(newestAt, result[1].CreatedAtUtc);
            Assert.Equal(olderAt, result[2].CreatedAtUtc);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
