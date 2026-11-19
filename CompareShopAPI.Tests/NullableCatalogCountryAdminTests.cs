using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class NullableCatalogCountryAdminTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Admin_reads_updates_and_deletes_catalog_rows_during_nullable_country_stage()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"NullableCatalogCountry_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;

        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync("20261118170350_AddCountryAndNullableCatalogCountryIds");
            db.Countries.Add(new Country("VN", "Vietnam"));
            await db.SaveChangesAsync();
            var countryId = await db.Countries.Where(country => country.Code == "VN").Select(country => country.Id).SingleAsync();
            var subject = $"nullable-admin-{Guid.NewGuid():N}";
            var admin = new User(subject, DateTime.UtcNow);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            var superAdminRoleId = await db.AdminRoles.Where(role => role.Name == "Super Admin").Select(role => role.Id).SingleAsync();
            db.AdminUserRoles.Add(new AdminUserRole { UserId = admin.Id, RoleId = superAdminRoleId });
            await db.SaveChangesAsync();

            var categorySlug = $"nullable-category-{Guid.NewGuid():N}";
            var deleteCategorySlug = $"nullable-category-delete-{Guid.NewGuid():N}";
            var shopSlug = $"nullable-shop-{Guid.NewGuid():N}";
            var deleteShopSlug = $"nullable-shop-delete-{Guid.NewGuid():N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Categories] ([Slug], [Name], [IsActive], [CountryId]) VALUES ({categorySlug}, N'Legacy Category', 1, NULL)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Categories] ([Slug], [Name], [IsActive], [CountryId]) VALUES ({deleteCategorySlug}, N'Delete Category', 1, NULL)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [PartnerShops] ([Slug], [Name], [WebsiteUrl], [IsActive], [CountryId]) VALUES ({shopSlug}, N'Legacy Shop', N'https://shop.example', 1, NULL)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [PartnerShops] ([Slug], [Name], [WebsiteUrl], [IsActive], [CountryId]) VALUES ({deleteShopSlug}, N'Delete Shop', N'https://shop.example', 1, NULL)");
            var categoryId = await db.Database.SqlQuery<int>($"SELECT [Id] AS [Value] FROM [Categories] WHERE [Slug] = {categorySlug}").SingleAsync();
            var deleteCategoryId = await db.Database.SqlQuery<int>($"SELECT [Id] AS [Value] FROM [Categories] WHERE [Slug] = {deleteCategorySlug}").SingleAsync();
            var shopId = await db.Database.SqlQuery<int>($"SELECT [Id] AS [Value] FROM [PartnerShops] WHERE [Slug] = {shopSlug}").SingleAsync();
            var deleteShopId = await db.Database.SqlQuery<int>($"SELECT [Id] AS [Value] FROM [PartnerShops] WHERE [Slug] = {deleteShopSlug}").SingleAsync();

            var data = new EfAdminData(db, TimeProvider.System);
            var service = new AdminService(data);
            var category = await service.Category(subject, categoryId, default);
            Assert.NotNull(category);
            Assert.Null(category.CountryId);
            Assert.Null(category.CountryCode);
            Assert.Null(category.CountryName);

            var shop = await service.Shop(subject, shopId, default);
            Assert.NotNull(shop);
            Assert.Null(shop.CountryId);
            Assert.Null(shop.CountryCode);
            Assert.Null(shop.CountryName);
            Assert.Contains((await service.Shops(subject, 1, 100, default)).Items, item => item.Id == shopId && item.CountryId is null);

            var updatedCategory = await service.UpdateCategory(subject, categoryId,
                new CategoryInput(categorySlug, "Updated Category", CountryId: countryId), default);
            Assert.Equal(countryId, updatedCategory.CountryId);
            Assert.Equal("VN", updatedCategory.CountryCode);
            Assert.Equal("Updated Category", updatedCategory.Name);

            var updatedShop = await service.UpdateShop(subject, shopId,
                new ShopInput(shopSlug, "Updated Shop", "https://shop.example", ShowOnHome: true, CountryId: countryId)
                {
                    HomeDisplayOrder = 4
                }, default);
            Assert.Equal(countryId, updatedShop.CountryId);
            Assert.Equal("VN", updatedShop.CountryCode);
            Assert.Equal("Updated Shop", updatedShop.Name);
            Assert.True(updatedShop.ShowOnHome);
            Assert.Equal(4, updatedShop.HomeDisplayOrder);

            Assert.NotNull(await service.Category(subject, deleteCategoryId, default));
            await service.DeleteCategory(subject, deleteCategoryId, default);
            Assert.Null(await service.Category(subject, deleteCategoryId, default));
            Assert.NotNull(await service.Shop(subject, deleteShopId, default));
            await service.DeleteShop(subject, deleteShopId, default);
            Assert.Null(await service.Shop(subject, deleteShopId, default));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
