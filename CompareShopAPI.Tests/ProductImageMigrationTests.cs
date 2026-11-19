using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class ProductImageMigrationTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Migration_backfills_files_and_external_sources_without_changing_legacy_image_url()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"ProductImages_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var fileId = Guid.NewGuid();
        const string externalUrl = "https://images.example.test/legacy.jpg";
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync("20261115070000_AddHomepagePartnerVisibility");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO [Categories] ([Slug],[Name],[IsActive],[Level],[SortOrder]) VALUES (N'legacy-parent',N'Legacy parent',1,1,0); INSERT INTO [Categories] ([Slug],[Name],[IsActive],[Level],[SortOrder],[ParentCategoryId]) VALUES (N'legacy-category',N'Legacy category',1,2,0,SCOPE_IDENTITY());");
                var categoryId = await db.Database.SqlQueryRaw<int>("SELECT [Id] AS [Value] FROM [Categories] WHERE [Slug] = N'legacy-category'").SingleAsync();
                var fileProduct = new Product { Slug = "legacy-files", Name = "Legacy Files", CategoryId = categoryId, ImageUrl = $"/files/{fileId:D}" };
                var externalProduct = new Product { Slug = "legacy-external", Name = "Legacy external", CategoryId = categoryId, ImageUrl = externalUrl };
                db.Products.AddRange(fileProduct, externalProduct);
                await db.SaveChangesAsync();
                await MigrationTestHelpers.MigrateToLatestAsync(db);

                var fileImage = await db.ProductImages.SingleAsync(x => x.ProductId == fileProduct.Id);
                var externalImage = await db.ProductImages.SingleAsync(x => x.ProductId == externalProduct.Id);
                Assert.Equal(fileId, fileImage.FileId);
                Assert.Null(fileImage.ExternalUrl);
                Assert.Null(externalImage.FileId);
                Assert.Equal(externalUrl, externalImage.ExternalUrl);
                Assert.Equal($"/files/{fileId:D}", (await db.Products.SingleAsync(x => x.Id == fileProduct.Id)).ImageUrl);
                Assert.Equal(externalUrl, (await db.Products.SingleAsync(x => x.Id == externalProduct.Id)).ImageUrl);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
