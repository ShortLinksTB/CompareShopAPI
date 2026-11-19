using CompareShopAPI.Api.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class LegacyCountrySeedMigrationTests(SqlServerTestFixture sqlServer)
{
    private const string CountrySchemaStage = "20261118170350_AddCountryAndNullableCatalogCountryIds";
    private const string LegacyCountrySeedStage = "20261118170355_AssignLegacyCatalogSeedRowsToVN";
    private const string RequiredCountryStage = "20261118170400_RequireCatalogCountryAssignments";

    [Fact]
    public async Task Maps_only_the_two_approved_legacy_seed_rows_to_Vietnam()
    {
        await using var db = await CreateDatabaseAsync();
        await PrepareCountryStageAsync(db);

        await db.Database.MigrateAsync(LegacyCountrySeedStage);

        var vnId = await db.Countries.Where(country => country.Code == "VN").Select(country => country.Id).SingleAsync();
        Assert.Equal(vnId, await db.PartnerShops.Where(shop => shop.Slug == "cellphones").Select(shop => shop.CountryId).SingleAsync());
        Assert.Equal(vnId, await db.Categories.Where(category => category.Slug == "electronics").Select(category => category.CountryId).SingleAsync());
        await db.Database.MigrateAsync(RequiredCountryStage);
    }

    [Fact]
    public async Task Missing_approved_fixture_rows_are_no_ops()
    {
        await using var db = await CreateDatabaseAsync();
        await PrepareCountryStageAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE [DataSources] SET [PartnerShopId] = NULL WHERE [PartnerShopId] = (SELECT [Id] FROM [PartnerShops] WHERE [Slug] = N'cellphones'); DELETE FROM [PartnerShops] WHERE [Slug] = N'cellphones'; DELETE FROM [Categories] WHERE [Slug] = N'electronics';");

        await db.Database.MigrateAsync(LegacyCountrySeedStage);

        Assert.Empty(await db.PartnerShops.Where(shop => shop.Slug == "cellphones").ToListAsync());
        Assert.Empty(await db.Categories.Where(category => category.Slug == "electronics").ToListAsync());
        await db.Database.MigrateAsync(RequiredCountryStage);
    }

    [Fact]
    public async Task Required_country_stage_fails_closed_for_other_unmapped_catalog_rows()
    {
        await using var db = await CreateDatabaseAsync();
        await PrepareCountryStageAsync(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO [PartnerShops] ([Slug], [Name], [IsActive], [CountryId]) VALUES (N'unmapped-shop', N'Unmapped shop', 1, NULL)");
        await db.Database.MigrateAsync(LegacyCountrySeedStage);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync(RequiredCountryStage));

        Assert.Contains("Country assignment is required", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [PartnerShops] WHERE [Slug]=N'unmapped-shop' AND [CountryId] IS NULL").SingleAsync());
        var isNullable = await db.Database.SqlQueryRaw<bool>("SELECT is_nullable AS [Value] FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PartnerShops') AND name=N'CountryId'").SingleAsync();
        Assert.True(isNullable);
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("category")]
    public async Task Duplicate_approved_fixture_slug_fails_migration(string fixtureType)
    {
        await using var db = await CreateDatabaseAsync();
        await PrepareCountryStageAsync(db);
        var idCountryId = await db.Countries.Where(country => country.Code == "ID").Select(country => country.Id).SingleAsync();
        if (fixtureType == "shop")
            await db.Database.ExecuteSqlRawAsync("DROP INDEX [IX_PartnerShops_Slug] ON [PartnerShops]; CREATE INDEX [IX_PartnerShops_Slug] ON [PartnerShops] ([Slug]);");
        var duplicateSql = fixtureType == "shop"
            ? "INSERT INTO [PartnerShops] ([Slug], [Name], [IsActive], [CountryId]) VALUES (N'cellphones', N'Duplicate', 1, {0})"
            : "INSERT INTO [Categories] ([Slug], [Name], [IsActive], [Level], [SortOrder], [CountryId]) VALUES (N'electronics', N'Duplicate', 1, 1, 0, {0})";
        await db.Database.ExecuteSqlRawAsync(duplicateSql, idCountryId);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync(LegacyCountrySeedStage));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("category")]
    public async Task Existing_non_Vietnam_assignment_fails_without_overwriting(string fixtureType)
    {
        await using var db = await CreateDatabaseAsync();
        await PrepareCountryStageAsync(db);
        var idCountryId = await db.Countries.Where(country => country.Code == "ID").Select(country => country.Id).SingleAsync();
        var conflictingSql = fixtureType == "shop"
            ? "UPDATE [PartnerShops] SET [CountryId] = {0} WHERE [Slug] = N'cellphones'"
            : "UPDATE [Categories] SET [CountryId] = {0} WHERE [Slug] = N'electronics'";
        await db.Database.ExecuteSqlRawAsync(conflictingSql, idCountryId);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync(LegacyCountrySeedStage));

        Assert.Contains("another Country", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(idCountryId, fixtureType == "shop"
            ? await db.PartnerShops.Where(shop => shop.Slug == "cellphones").Select(shop => shop.CountryId).SingleAsync()
            : await db.Categories.Where(category => category.Slug == "electronics").Select(category => category.CountryId).SingleAsync());
    }

    private async Task<CompareShopDbContext> CreateDatabaseAsync()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopCountryMigration_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        return new CompareShopDbContext(options);
    }

    private static async Task PrepareCountryStageAsync(CompareShopDbContext db)
    {
        await db.Database.MigrateAsync(CountrySchemaStage);
        await CountrySeed.SeedInitialAsync(db, typeof(CatalogSeedCommand).Assembly);
    }
}
