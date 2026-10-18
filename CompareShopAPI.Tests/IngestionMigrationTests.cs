using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class IngestionMigrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Migration_adds_only_ingestion_objects_and_seeds_bhx_without_requiring_shop_row()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopIngestion_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            var source = await db.DataSources.SingleAsync();
            Assert.Equal("bhx", source.Key);
            Assert.Equal("/bia", source.CategoryPath);
            Assert.Equal(72, source.IntervalHours);
            Assert.Null(source.PartnerShopId);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'SourceItemMappings'").SingleAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'ShopeeDataSources'").SingleAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
