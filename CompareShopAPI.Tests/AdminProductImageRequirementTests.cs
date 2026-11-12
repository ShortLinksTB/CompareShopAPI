using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminProductImageRequirementTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task ProductAndProductListDeriveNeedsStandardImageFromFilesUrl()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CompareShopProductImageRequirement_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            int filesProductId;
            int noImageProductId;
            int externalImageProductId;
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                var rootCategory = new Category { Slug = "image-requirement-root", Name = "Image Requirement Root", Level = 1 };
                db.Categories.Add(rootCategory);
                await db.SaveChangesAsync();
                var category = new Category
                {
                    Slug = "image-requirement-category", Name = "Image Requirement", Level = 2,
                    ParentCategoryId = rootCategory.Id
                };
                db.Categories.Add(category);
                await db.SaveChangesAsync();

                var filesProduct = new Product
                {
                    Slug = "files-image-product", Name = "Files image", CategoryId = category.Id,
                    ImageUrl = $"/files/{Guid.NewGuid():D}"
                };
                var noImageProduct = new Product
                {
                    Slug = "no-image-product", Name = "No image", CategoryId = category.Id, ImageUrl = null
                };
                var externalImageProduct = new Product
                {
                    Slug = "external-image-product", Name = "External image", CategoryId = category.Id,
                    ImageUrl = "https://images.example.test/product.jpg"
                };
                db.Products.AddRange(filesProduct, noImageProduct, externalImageProduct);
                await db.SaveChangesAsync();
                filesProductId = filesProduct.Id;
                noImageProductId = noImageProduct.Id;
                externalImageProductId = externalImageProduct.Id;
            }

            await using (var db = new CompareShopDbContext(options))
            {
                var data = new EfAdminData(db, TimeProvider.System);
                var filesProduct = Assert.IsType<CompareShopAPI.Application.Admin.AdminProduct>(await data.Product(filesProductId, default));
                var noImageProduct = Assert.IsType<CompareShopAPI.Application.Admin.AdminProduct>(await data.Product(noImageProductId, default));
                var externalImageProduct = Assert.IsType<CompareShopAPI.Application.Admin.AdminProduct>(await data.Product(externalImageProductId, default));
                Assert.False(filesProduct.NeedsStandardImage);
                Assert.True(noImageProduct.NeedsStandardImage);
                Assert.True(externalImageProduct.NeedsStandardImage);

                var listedProducts = await data.Products(1, 10, null, default);
                Assert.False(Assert.Single(listedProducts.Items, x => x.Id == filesProductId).NeedsStandardImage);
                Assert.True(Assert.Single(listedProducts.Items, x => x.Id == noImageProductId).NeedsStandardImage);
                Assert.True(Assert.Single(listedProducts.Items, x => x.Id == externalImageProductId).NeedsStandardImage);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
