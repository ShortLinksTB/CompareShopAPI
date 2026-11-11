using CompareShopAPI.Api.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class MatchingFixtureCommandTests(SqlServerTestFixture sqlServer)
{
    [Theory]
    [InlineData("seed-matching-fixtures")]
    [InlineData("cleanup-matching-fixtures")]
    public async Task Fixture_commands_require_development(string command)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            [command], Environment("Production"), Configuration(true, "Server=localhost;Database=CompareShop"),
            new ServiceCollection().BuildServiceProvider()));

        Assert.Contains("Development", exception.Message);
    }

    [Fact]
    public async Task Fixture_commands_require_explicit_opt_in_and_the_compare_shop_catalog()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            ["seed-matching-fixtures"], Environment("Development"), Configuration(false, Connection("CompareShop")), services));
        Assert.Contains("disabled", disabled.Message);

        var wrongCatalog = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            ["seed-matching-fixtures"], Environment("Development"), Configuration(true, Connection("OtherDb")), services));
        Assert.Contains("CompareShop", wrongCatalog.Message);

        var missingConnection = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            ["seed-matching-fixtures"], Environment("Development"), Configuration(true, ""), services));
        Assert.Contains("CompareShop", missingConnection.Message);
    }

    [Fact]
    public async Task Non_fixture_arguments_are_ignored()
    {
        Assert.False(MatchingFixtureCommand.IsRequested([]));
        Assert.False(MatchingFixtureCommand.IsRequested(["seed-matching-fixtures", "extra"]));
        Assert.False(await MatchingFixtureCommand.TryRunAsync(["not-a-command"], Environment("Development"),
            Configuration(true, Connection("CompareShop")), new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public async Task Fixture_commands_require_the_allowlisted_dev_server_even_when_catalog_matches()
    {
        var connection = "Server=other-sql.example.test;Database=CompareShop;User Id=sa;Password=test;Encrypt=False";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            ["seed-matching-fixtures"], Environment("Development"), Configuration(true, connection, "mssql.shortlink-infra-dev.svc.cluster.local"),
            new ServiceCollection().BuildServiceProvider()));
        Assert.Contains("allowlisted dev SQL server", exception.Message);
    }

    [Fact]
    public async Task Fixture_command_refuses_an_unreachable_compare_shop_database()
    {
        const string connection = "Server=127.0.0.1,1;Database=CompareShop;User Id=sa;Password=test;Connect Timeout=1;ConnectRetryCount=0;Encrypt=False";
        await using var services = new ServiceCollection()
            .AddDbContext<CompareShopDbContext>(options => options.UseSqlServer(connection))
            .BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
            ["seed-matching-fixtures"], Environment("Development"), Configuration(true, connection), services));

        Assert.Contains("unreachable", exception.Message);
    }

    [Fact]
    public async Task Seed_and_cleanup_are_idempotent_and_remove_only_fixture_rows()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = "CompareShop" }.ConnectionString;
        var services = new ServiceCollection()
            .AddDbContext<CompareShopDbContext>(options => options.UseSqlServer(connection))
            .BuildServiceProvider();
        var config = Configuration(true, connection);

        await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            await db.Database.MigrateAsync();

        try
        {
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            var firstSeedCounts = await FixtureCounts(services);
            Assert.Equal((1, 2, 3, 2, 4, 4), firstSeedCounts);

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal(firstSeedCounts, await FixtureCounts(services));

            // Same namespace text is not ownership. This row is created outside the fixture manifest.
            var unowned = new ProductMatching("QC-SL1815 unowned row", null, DateTime.UtcNow);
            await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            {
                db.ProductMatchings.Add(unowned);
                await db.SaveChangesAsync();
            }

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 1), await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            {
                db.ProductMatchings.Remove(unowned);
                await db.SaveChangesAsync();
            }
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 0), await FixtureCounts(services));
        }
        finally
        {
            await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services);
            await services.DisposeAsync();
        }
    }

    private static IConfiguration Configuration(bool allow, string connectionString, string? allowedServer = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:AllowMatchingFixtureSeed"] = allow.ToString(),
            ["ConnectionStrings:CompareShopDb"] = connectionString,
            ["Database:MatchingFixtureAllowedServer"] = allowedServer ?? new SqlConnectionStringBuilder(connectionString).DataSource
        })
        .Build();

    private static string Connection(string database) => $"Server=localhost;Database={database};User Id=sa;Password=test;Encrypt=False";

    private static TestEnvironment Environment(string name) => new(name);

    private static async Task<(int Products, int Shops, int DataSources, int Offers, int Mappings, int Matchings)> FixtureCounts(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        return (
            await db.Products.CountAsync(x => x.Slug.StartsWith("qc-sl1815-")),
            await db.PartnerShops.CountAsync(x => x.Slug.StartsWith("qc-sl1815-")),
            await db.DataSources.CountAsync(x => x.Key.StartsWith("qc-sl1815-")),
            await db.ProductOffers.CountAsync(x => x.Product.Slug.StartsWith("qc-sl1815-") || x.PartnerShop.Slug.StartsWith("qc-sl1815-")),
            await db.SourceItemMappings.CountAsync(x => x.ExternalSku.StartsWith("QC-SL1815")),
            await db.ProductMatchings.CountAsync(x => x.Name.StartsWith("QC-SL1815")));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompareShopAPI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
