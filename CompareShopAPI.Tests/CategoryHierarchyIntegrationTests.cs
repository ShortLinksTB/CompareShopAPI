using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class CategoryHierarchyIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Migration_backfills_legacy_slugs_without_changing_product_category_ids_or_qc_fixture()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopCategoryMigration_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;

        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync("20261105170000_GrantOffersCreateToOperator");

            foreach (var (slug, name) in new[]
                     {
                         ("phones", "Phones"),
                         ("computers", "Computers"),
                         ("audio", "Audio")
                     })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Categories] ([Slug], [Name], [IsActive]) VALUES ({slug}, {name}, 1)");
            }
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Categories] ON; INSERT INTO [Categories] ([Id], [Slug], [Name], [IsActive]) VALUES (1003, N'qc-mcp-live-mw0usaj6', N'QC MCP live mw0usaj6', 1); SET IDENTITY_INSERT [Categories] OFF;");

            foreach (var slug in new[] { "phones", "computers", "audio" })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Products] ([Slug], [Name], [CategoryId], [IsActive]) SELECT {"legacy-" + slug}, {"Legacy " + slug}, [Id], 1 FROM [Categories] WHERE [Slug] = {slug}");
            }

            var originalCategoryIds = new Dictionary<string, int>();
            foreach (var slug in new[] { "phones", "computers", "audio", "qc-mcp-live-mw0usaj6" })
                originalCategoryIds[slug] = await db.Database.SqlQuery<int>($"SELECT [Id] AS [Value] FROM [Categories] WHERE [Slug] = {slug}").SingleAsync();
            var originalProductCategoryIds = await db.Database.SqlQueryRaw<int>("SELECT [CategoryId] AS [Value] FROM [Products] ORDER BY [Slug]").ToArrayAsync();
            var qcFixtureId = originalCategoryIds["qc-mcp-live-mw0usaj6"];

            await db.Database.MigrateAsync();

            var electronics = await db.Categories.SingleAsync(category => category.Slug == "electronics");
            Assert.Equal(1, electronics.Level);
            Assert.Null(electronics.ParentCategoryId);
            foreach (var slug in new[] { "phones", "computers", "audio" })
            {
                var category = await db.Categories.SingleAsync(item => item.Slug == slug);
                Assert.Equal(originalCategoryIds[slug], category.Id);
                Assert.Equal(2, category.Level);
                Assert.Equal(electronics.Id, category.ParentCategoryId);
            }
            var storedProductCategoryIds = await db.Products.OrderBy(product => product.Slug).Select(product => product.CategoryId).ToArrayAsync();
            Assert.Equal(originalProductCategoryIds, storedProductCategoryIds);

            var qcFixture = await db.Categories.SingleAsync(category => category.Id == qcFixtureId);
            Assert.Equal("qc-mcp-live-mw0usaj6", qcFixture.Slug);
            Assert.True(qcFixture.IsActive);
            Assert.Equal(1, qcFixture.Level);
            Assert.Null(qcFixture.ParentCategoryId);

            await Assert.ThrowsAsync<SqlException>(async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [Categories] ([Slug], [Name], [IsActive], [Level], [SortOrder], [ParentCategoryId]) VALUES (N'invalid-level', N'Invalid', 1, 2, 0, NULL)"));
            await Assert.ThrowsAsync<SqlException>(async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [Categories] ([Slug], [Name], [IsActive], [Level], [SortOrder], [ParentCategoryId]) VALUES (N'invalid-parent', N'Invalid', 1, 2, 0, 999999)"));
            await Assert.ThrowsAsync<SqlException>(async () => await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [Categories] WHERE [Id] = {electronics.Id}"));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
