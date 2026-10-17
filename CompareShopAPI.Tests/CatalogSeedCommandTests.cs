using CompareShopAPI.Api.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CatalogSeedCommandTests
{
    [Fact]
    public async Task Non_command_arguments_do_not_start_seeding()
    {
        var result = await CatalogSeedCommand.TryRunAsync([], Environment("Production"), Configuration(true, Connection("CompareShop")),
            new ServiceCollection().BuildServiceProvider(), NullLogger.Instance);

        Assert.False(result);
    }

    [Fact]
    public async Task Seed_command_is_rejected_outside_development_even_when_opted_in()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogSeedCommand.TryRunAsync(
            ["seed-catalog"], Environment("Production"), Configuration(true, Connection("CompareShop")),
            new ServiceCollection().BuildServiceProvider(), NullLogger.Instance));

        Assert.Contains("Development", exception.Message);
    }

    [Fact]
    public async Task Seed_command_requires_explicit_opt_in_and_the_compare_shop_database()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogSeedCommand.TryRunAsync(
            ["seed-catalog"], Environment("Development"), Configuration(false, Connection("CompareShop")), services, NullLogger.Instance));
        Assert.Contains("disabled", disabled.Message);

        var wrongDatabase = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogSeedCommand.TryRunAsync(
            ["seed-catalog"], Environment("Development"), Configuration(true, Connection("OtherDb")), services, NullLogger.Instance));
        Assert.Contains("CompareShop", wrongDatabase.Message);

        var missingConnection = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogSeedCommand.TryRunAsync(
            ["seed-catalog"], Environment("Development"), Configuration(true, ""), services, NullLogger.Instance));
        Assert.Contains("CompareShop", missingConnection.Message);
    }

    [Fact]
    public async Task Seed_command_rejects_an_unreachable_compare_shop_database()
    {
        var services = new ServiceCollection()
            .AddDbContext<CompareShopDbContext>(options => options.UseSqlServer(
                "Server=127.0.0.1,1;Database=CompareShop;User Id=sa;Password=test;Connect Timeout=1;ConnectRetryCount=0;Encrypt=False"))
            .BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogSeedCommand.TryRunAsync(
            ["seed-catalog"], Environment("Development"), Configuration(true, Connection("CompareShop")), services, NullLogger.Instance));

        Assert.Contains("Cannot connect", exception.Message);
    }

    [Fact]
    public async Task Seed_command_populates_repeatably_in_development()
    {
        var databaseName = $"catalog-seed-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddDbContext<CompareShopDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var configuration = Configuration(true, Connection("CompareShop"));

        Assert.True(await CatalogSeedCommand.TryRunAsync(["seed-catalog"], Environment("Development"), configuration, services, NullLogger.Instance));
        var initialCounts = await ReadCountsAsync(services);
        Assert.True(await CatalogSeedCommand.TryRunAsync(["seed-catalog"], Environment("Development"), configuration, services, NullLogger.Instance));
        Assert.Equal(initialCounts, await ReadCountsAsync(services));
        Assert.Equal(30, initialCounts.ActiveProducts);
        Assert.Equal(3, initialCounts.Categories);
        Assert.Equal(3, initialCounts.ActiveShops);
        Assert.True(initialCounts.Offers >= 50);
        Assert.True(initialCounts.InactiveProducts >= 1);
        Assert.True(initialCounts.ProductsWithoutOffers >= 1);
    }

    private static IConfiguration Configuration(bool allow, string connectionString) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:AllowCatalogSeed"] = allow.ToString(),
            ["ConnectionStrings:CompareShopDb"] = connectionString
        })
        .Build();

    private static string Connection(string database) => $"Server=localhost;Database={database};User Id=sa;Password=test;Encrypt=False";

    private static TestEnvironment Environment(string name) => new(name);

    private static async Task<(int ActiveProducts, int Categories, int ActiveShops, int Offers, int InactiveProducts, int ProductsWithoutOffers)> ReadCountsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        return (
            await db.Products.CountAsync(x => x.IsActive),
            await db.Categories.CountAsync(),
            await db.PartnerShops.CountAsync(x => x.IsActive),
            await db.ProductOffers.CountAsync(),
            await db.Products.CountAsync(x => !x.IsActive),
            await db.Products.CountAsync(x => x.IsActive && !db.ProductOffers.Any(offer => offer.ProductId == x.Id)));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompareShopAPI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
