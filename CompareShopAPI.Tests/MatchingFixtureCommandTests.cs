using CompareShopAPI.Api.Admin;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
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

        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        int laptopCategoryId;
        await using (var db = new CompareShopDbContext(options))
        {
            await db.Database.MigrateAsync();
            await Assert.ThrowsAsync<MatchingLaptopCategoryUnavailableException>(() => MatchingFixtureCommand.TryRunAsync(
                ["seed-matching-fixtures"], Environment("Development"), config, services));
            Assert.DoesNotContain(await db.Categories.ToListAsync(), x => x.Name.Contains("laptop", StringComparison.OrdinalIgnoreCase) || x.Slug.Contains("laptop", StringComparison.OrdinalIgnoreCase));
            Assert.False(await db.PartnerShops.AnyAsync(x => x.Slug.StartsWith("qc-sl1815-")));
            Assert.False(await db.ProductMatchings.AnyAsync(x => x.Name.StartsWith("QC-SL1815")));
            laptopCategoryId = (await CategoryTestData.AddLeafAsync(db, "laptop", "Laptop")).Id;
        }

        try
        {
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            var firstSeedCounts = await FixtureCounts(services);
            Assert.Equal((1, 2, 3, 2, 4, 7, 1), firstSeedCounts);
            await using (var verify = new CompareShopDbContext(options))
            {
                Assert.Equal(3, await verify.Categories.CountAsync());
                Assert.All(await verify.Products.ToListAsync(), x => Assert.Equal(laptopCategoryId, x.CategoryId));
            }

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal(firstSeedCounts, await FixtureCounts(services));

            // Exercise the same application service used by the HTTP API: no-image creation
            // reports the standard-image warning, and an invalid source mapping rolls back all writes.
            var subject = $"qc-fixture-test-{Guid.NewGuid():N}";
            int createdProductId;
            int rollbackMatchingId;
            await using (var db = new CompareShopDbContext(options))
            {
                var user = new User(subject, DateTime.UtcNow);
                var role = new AdminRole { Name = $"qc-fixture-{Guid.NewGuid():N}" };
                db.Users.Add(user); db.AdminRoles.Add(role);
                await db.SaveChangesAsync();
                var permissions = await db.AdminPermissions.Where(x => x.Key == AdminPermissions.MatchingApprove || x.Key == AdminPermissions.ProductsCreate).ToListAsync();
                db.AdminRolePermissions.AddRange(permissions.Select(x => new AdminRolePermission { RoleId = role.Id, PermissionId = x.Id }));
                db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
                await db.SaveChangesAsync();

                var data = new EfAdminData(db, TimeProvider.System);
                var service = new AdminService(data);
                var groupMembers = await db.ProductMatchings.Where(x => x.ExternalSku == "QC-SL1815-shop-a-laptop" || x.ExternalSku == "QC-SL1815-shop-b-laptop")
                    .OrderBy(x => x.ExternalSku).ToListAsync();
                Assert.Equal(2, groupMembers.Count);
                var groupKeys = (await data.MatchingQueue(default)).Where(x => groupMembers.Select(m => m.Id).Contains(x.Id)).Select(x => x.GroupKey).Distinct().ToArray();
                Assert.Single(groupKeys);
                Assert.NotNull(groupKeys[0]);
                await service.ApproveMatchingGroup(subject, groupMembers.Select(x => x.Id).ToArray(),
                    (await db.Products.SingleAsync(x => x.Slug == "qc-sl1815-existing-product")).Id, default);
                Assert.True(await db.AdminAuditLogs.AnyAsync(x => x.Action == "approve_group" && x.ResourceType == "matching_group"));

                var single = await db.ProductMatchings.SingleAsync(x => x.Name.Contains("nullable relation"));
                var existingProductId = (await db.Products.SingleAsync(x => x.Slug == "qc-sl1815-existing-product")).Id;
                await service.ApproveMatching(subject, single.Id, existingProductId, default);
                Assert.True(await db.AdminAuditLogs.AnyAsync(x => x.Action == "approve" && x.ResourceType == "matching" && x.ResourceId == single.Id.ToString()));

                var matching = await db.ProductMatchings.SingleAsync(x => x.ExternalSku == "QC-SL1815-missing-shop-laptop");
                var created = await service.CreateMatchingProduct(subject, matching.Id,
                    new MatchingProductCreate("QC Created From Matching", "ASUS", null, laptopCategoryId, null, "none"), default);
                Assert.True(created.NeedsStandardImage);
                Assert.Equal(laptopCategoryId, created.CategoryId);
                createdProductId = created.Id;

                var missingMapping = await db.ProductMatchings.SingleAsync(x => x.Name.Contains("missing mapping"));
                rollbackMatchingId = missingMapping.Id;
                var productsBeforeFailure = await db.Products.CountAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateMatchingProduct(subject, missingMapping.Id,
                    new MatchingProductCreate("QC Rollback Product", "ASUS", null, laptopCategoryId, null, "none"), default));
                await using var verifyRollback = new CompareShopDbContext(options);
                Assert.Equal(productsBeforeFailure, await verifyRollback.Products.CountAsync());
                Assert.Equal("pending", (await verifyRollback.ProductMatchings.SingleAsync(x => x.Id == rollbackMatchingId)).Status);
                Assert.False(await verifyRollback.MatchingDecisions.AnyAsync(x => x.MatchingId == rollbackMatchingId));
                Assert.False(await verifyRollback.AdminAuditLogs.AnyAsync(x => x.Action == "create_from_matching" && x.AfterJson!.Contains($"\"matchingIds\":[{rollbackMatchingId}]")));
            }

            // Same namespace text is not ownership. This row is created outside the fixture manifest.
            var unowned = new ProductMatching("QC-SL1815 unowned row", null, DateTime.UtcNow);
            await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            {
                db.ProductMatchings.Add(unowned);
                await db.SaveChangesAsync();
            }

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 1, 0), await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            {
                Assert.False(await db.Products.AnyAsync(x => x.Id == createdProductId));
                Assert.True(await db.AdminAuditLogs.AnyAsync(x => x.Action == "create_from_matching" && x.AfterJson!.Contains($"\"productId\":{createdProductId}")));
                db.ProductMatchings.Remove(unowned);
                await db.SaveChangesAsync();
            }
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 0, 0), await FixtureCounts(services));
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

    private static async Task<(int Products, int Shops, int DataSources, int Offers, int Mappings, int Matchings, int Groups)> FixtureCounts(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        var groupKeys = (await new EfAdminData(db, TimeProvider.System).MatchingQueue(default)).Where(x => x.Name.StartsWith("QC-SL1815", StringComparison.Ordinal))
            .Select(x => x.GroupKey).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return (
            await db.Products.CountAsync(x => x.Slug.StartsWith("qc-sl1815-")),
            await db.PartnerShops.CountAsync(x => x.Slug.StartsWith("qc-sl1815-")),
            await db.DataSources.CountAsync(x => x.Key.StartsWith("qc-sl1815-")),
            await db.ProductOffers.CountAsync(x => x.Product.Slug.StartsWith("qc-sl1815-") || x.PartnerShop.Slug.StartsWith("qc-sl1815-")),
            await db.SourceItemMappings.CountAsync(x => x.ExternalSku.StartsWith("QC-SL1815")),
            await db.ProductMatchings.CountAsync(x => x.Name.StartsWith("QC-SL1815")), groupKeys);
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompareShopAPI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
