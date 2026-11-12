using CompareShopAPI.Api.Admin;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var staleMappings = await db.SourceItemMappings.Where(x => x.ExternalSku.StartsWith("QC-SL1815-")).Select(x => x.Id).ToArrayAsync();
            await db.SourceItemMappings.Where(x => staleMappings.Contains(x.Id)).ExecuteDeleteAsync();
            await db.ProductMatchings.Where(x => x.Name.StartsWith("QC-SL1815 ")).ExecuteDeleteAsync();
            await db.ProductOffers.Where(x => x.PartnerShop.Slug.StartsWith("qc-sl1815-") || x.Product.Slug.StartsWith("qc-sl1815-")).ExecuteDeleteAsync();
            await db.Products.Where(x => x.Slug.StartsWith("qc-sl1815-")).ExecuteDeleteAsync();
            await db.DataSources.Where(x => x.Key.StartsWith("qc-sl1815-")).ExecuteDeleteAsync();
            await db.PartnerShops.Where(x => x.Slug.StartsWith("qc-sl1815-")).ExecuteDeleteAsync();
            await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture").ExecuteDeleteAsync();
            var sharedLaptop = await db.Categories.SingleOrDefaultAsync(x => x.Id == 4008);
            if (sharedLaptop is null)
            {
                Assert.DoesNotContain(await db.Categories.ToListAsync(), x => x.Name.Contains("laptop", StringComparison.OrdinalIgnoreCase) || x.Slug.Contains("laptop", StringComparison.OrdinalIgnoreCase));
                await Assert.ThrowsAsync<MatchingLaptopCategoryUnavailableException>(() => MatchingFixtureCommand.TryRunAsync(
                    ["seed-matching-fixtures"], Environment("Development"), config, services));
                Assert.False(await db.PartnerShops.AnyAsync(x => x.Slug.StartsWith("qc-sl1815-")));
                Assert.False(await db.ProductMatchings.AnyAsync(x => x.Name.StartsWith("QC-SL1815")));
                var laptopParent = new Category { Slug = "test-laptop-parent", Name = "Test laptop parent", Level = 1 };
                db.Categories.Add(laptopParent);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"SET IDENTITY_INSERT [Categories] ON; INSERT INTO [Categories] ([Id], [Slug], [Name], [IsActive], [Level], [SortOrder], [ParentCategoryId]) VALUES (4008, N'laptop', N'Laptop', 1, 2, 0, {laptopParent.Id}); SET IDENTITY_INSERT [Categories] OFF;");
            }
            else Assert.True(sharedLaptop.Name.Contains("laptop", StringComparison.OrdinalIgnoreCase) || sharedLaptop.Slug.Contains("laptop", StringComparison.OrdinalIgnoreCase));
            laptopCategoryId = 4008;
        }

        try
        {
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            var firstSeedCounts = await FixtureCounts(services);
            Assert.Equal((1, 2, 3, 2, 7, 10, 2), firstSeedCounts);
            await using (var verify = new CompareShopDbContext(options))
            {
                Assert.Equal(3, await verify.Categories.CountAsync());
                Assert.All(await verify.Products.ToListAsync(), x => Assert.Equal(laptopCategoryId, x.CategoryId));
            }

            int[] originalMappingIds;
            int[] originalMatchingIds;
            await using (var db = new CompareShopDbContext(options))
            {
                var manifestRow = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                var manifestJson = JsonNode.Parse(manifestRow.AfterJson!)!.AsObject();
                var allMappingIds = manifestJson["MappingIds"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
                var allMatchingIds = manifestJson["MatchingIds"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
                originalMappingIds = allMappingIds[..^3];
                originalMatchingIds = allMatchingIds[..^3];
                var appendedMappingIds = allMappingIds.Except(originalMappingIds).ToArray();
                var appendedMatchingIds = allMatchingIds.Except(originalMatchingIds).ToArray();
                await db.SourceItemMappings.Where(x => appendedMappingIds.Contains(x.Id)).ExecuteDeleteAsync();
                await db.ProductMatchings.Where(x => appendedMatchingIds.Contains(x.Id)).ExecuteDeleteAsync();
                manifestJson["MappingIds"] = JsonSerializer.SerializeToNode(originalMappingIds);
                manifestJson["MatchingIds"] = JsonSerializer.SerializeToNode(originalMatchingIds);
                manifestJson.Remove("Version"); // PR #80 manifests are unversioned.
                db.AdminAuditLogs.Add(new AdminAuditLog
                {
                    ActorSubject = "QC-SL1815", Action = "fixture.manifest.seeded", ResourceType = "QC-SL1815-MatchingFixture",
                    ResourceId = "QC-SL1815", CreatedAtUtc = DateTime.UtcNow, AfterJson = manifestJson.ToJsonString()
                });
                await db.SaveChangesAsync();
            }

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal(firstSeedCounts, await FixtureCounts(services));

            int[] v2MatchingIds;
            int[] v2MappingIds;
            int[] v2ShopIds;
            int[] v2SourceIds;
            using var v2Snapshot = await ReadLatestManifest(options);
            v2MatchingIds = v2Snapshot.RootElement.GetProperty("MatchingIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            v2MappingIds = v2Snapshot.RootElement.GetProperty("MappingIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            v2ShopIds = v2Snapshot.RootElement.GetProperty("ShopIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            v2SourceIds = v2Snapshot.RootElement.GetProperty("DataSourceIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            var v3Counts = await FixtureCounts(services);
            Assert.Equal((2, 3, 4, 3, 8, 11, 2), v3Counts);
            await using (var verifyV3 = new CompareShopDbContext(options))
            {
                var latest = await verifyV3.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                using var manifest = JsonDocument.Parse(latest.AfterJson!);
                var root = manifest.RootElement;
                Assert.Equal(3, root.GetProperty("Version").GetInt32());
                Assert.Equal(v2MatchingIds, root.GetProperty("MatchingIds").EnumerateArray().Select(x => x.GetInt32()).Take(v2MatchingIds.Length));
                Assert.Equal(v2MappingIds, root.GetProperty("MappingIds").EnumerateArray().Select(x => x.GetInt32()).Take(v2MappingIds.Length));
                Assert.Equal(v2ShopIds, root.GetProperty("ShopIds").EnumerateArray().Select(x => x.GetInt32()).Take(v2ShopIds.Length));
                Assert.Equal(v2SourceIds, root.GetProperty("DataSourceIds").EnumerateArray().Select(x => x.GetInt32()).Take(v2SourceIds.Length));
                var v3MatchingId = Assert.Single(root.GetProperty("V3MatchingIds").EnumerateArray()).GetInt32();
                var row = await verifyV3.ProductMatchings.SingleAsync(x => x.Id == v3MatchingId);
                Assert.Equal("pending", row.Status);
                Assert.False(row.IsApproved);
                Assert.Null(row.ProductId);
                Assert.Equal(4008, (await verifyV3.Products.SingleAsync(x => x.Id == Assert.Single(root.GetProperty("V3ProductIds").EnumerateArray()).GetInt32())).CategoryId);
                var createdFromV3 = new Product { Slug = "qc-sl1815-v3-created-product", Name = "Created from v3 matching", CategoryId = 4008 };
                verifyV3.Products.Add(createdFromV3);
                await verifyV3.SaveChangesAsync();
                verifyV3.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = "qc-test", Action = "create_from_matching", ResourceType = "product",
                    ResourceId = createdFromV3.Id.ToString(), CreatedAtUtc = DateTime.UtcNow,
                    AfterJson = JsonSerializer.Serialize(new { productId = createdFromV3.Id, matchingIds = new[] { v3MatchingId } }) });
                await verifyV3.SaveChangesAsync();
                Assert.True(await verifyV3.Products.AnyAsync(x => x.Id == createdFromV3.Id));
            }
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["seed-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((v3Counts.Products + 1, v3Counts.Shops, v3Counts.DataSources, v3Counts.Offers, v3Counts.Mappings, v3Counts.Matchings, v3Counts.Groups), await FixtureCounts(services));
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal(firstSeedCounts, await FixtureCounts(services));
            await using (var verifyV2 = new CompareShopDbContext(options))
            {
                Assert.False(await verifyV2.Products.AnyAsync(x => x.Slug == "qc-sl1815-v3-created-product"));
                Assert.All(v2MatchingIds, id => Assert.True(verifyV2.ProductMatchings.Any(x => x.Id == id)));
                Assert.All(v2MappingIds, id => Assert.True(verifyV2.SourceItemMappings.Any(x => x.Id == id)));
                Assert.All(v2ShopIds, id => Assert.True(verifyV2.PartnerShops.Any(x => x.Id == id)));
                Assert.All(v2SourceIds, id => Assert.True(verifyV2.DataSources.Any(x => x.Id == id)));
                var v2Manifest = JsonNode.Parse(v2Snapshot.RootElement.GetRawText())!.AsObject();
                v2Manifest["Version"] = 2;
                foreach (var key in new[] { "V3ShopIds", "V3DataSourceIds", "V3OfferIds", "V3MappingIds", "V3MatchingIds", "V3ProductIds" }) v2Manifest.Remove(key);
                verifyV2.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = "QC-SL1815", Action = "fixture.manifest.seeded",
                    ResourceType = "QC-SL1815-MatchingFixture", ResourceId = "QC-SL1815", CreatedAtUtc = DateTime.UtcNow, AfterJson = v2Manifest.ToJsonString() });
                await verifyV2.SaveChangesAsync();
            }
            await using (var verifyUpgrade = new CompareShopDbContext(options))
            {
                var upgradedRow = await verifyUpgrade.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                using var upgradedJson = JsonDocument.Parse(upgradedRow.AfterJson!);
                var upgraded = upgradedJson.RootElement;
                Assert.Equal(2, upgraded.GetProperty("Version").GetInt32());
                Assert.All(originalMappingIds, id => Assert.Contains(id, upgraded.GetProperty("MappingIds").EnumerateArray().Select(x => x.GetInt32())));
                Assert.All(originalMatchingIds, id => Assert.Contains(id, upgraded.GetProperty("MatchingIds").EnumerateArray().Select(x => x.GetInt32())));
                Assert.Equal(originalMappingIds.Length + 3, upgraded.GetProperty("MappingIds").GetArrayLength());
            }

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

                var matching = await db.ProductMatchings.SingleAsync(x => x.ExternalSku == "QC-SL1815-v2-create-product");
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
            var beforeCleanupCounts = await FixtureCounts(services);

            int sourceId;
            int rootJobId;
            int runningJobId;
            int retryJobId;
            int finalRetryJobId;
            int unrelatedJobId;
            await using (var db = new CompareShopDbContext(options))
            {
                var sharedSourceId = (await db.DataSources.SingleAsync(x => x.Key == "bhx")).Id;
                var unrelated = new SyncJob(sharedSourceId, DateTime.UtcNow);
                db.SyncJobs.Add(unrelated);
                await db.SaveChangesAsync();
                unrelatedJobId = unrelated.Id;
                await FinishJob(db, unrelatedJobId, SyncJobState.Success);
            }
            await using (var db = new CompareShopDbContext(options))
            {
                sourceId = (await db.DataSources.SingleAsync(x => x.Key == "qc-sl1815-shop-a")).Id;
                var queued = new SyncJob(sourceId, DateTime.UtcNow);
                db.SyncJobs.Add(queued);
                await db.SaveChangesAsync();
                rootJobId = queued.Id;
            }

            var activeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                ["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Contains("active or non-terminal", activeFailure.Message);
            Assert.Equal(beforeCleanupCounts, await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(options))
            {
                Assert.True(await db.DataSources.AnyAsync(x => x.Id == sourceId));
                Assert.True(await db.SyncJobs.AnyAsync(x => x.Id == rootJobId));
                await FinishJob(db, rootJobId, SyncJobState.Success);
                var runningSourceId = (await db.DataSources.SingleAsync(x => x.Key == "qc-sl1815-shop-b")).Id;
                var running = new SyncJob(runningSourceId, DateTime.UtcNow);
                var now = DateTime.UtcNow;
                Assert.True(running.Start(now, "fixture-running-" + Guid.NewGuid().ToString("N"), now.AddMinutes(SyncJob.LeaseDurationMinutes)));
                db.SyncJobs.Add(running);
                await db.SaveChangesAsync();
                runningJobId = running.Id;
                var runningFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                    ["cleanup-matching-fixtures"], Environment("Development"), config, services));
                Assert.Contains("active or non-terminal", runningFailure.Message);
                await FinishJob(db, runningJobId, SyncJobState.Success);
                var retry = new SyncJob(sourceId, DateTime.UtcNow, rootJobId);
                db.SyncJobs.Add(retry);
                await db.SaveChangesAsync();
                retryJobId = retry.Id;
                await FinishJob(db, retryJobId, SyncJobState.Failed);
                var finalRetry = new SyncJob(sourceId, DateTime.UtcNow, retryJobId);
                db.SyncJobs.Add(finalRetry);
                await db.SaveChangesAsync();
                finalRetryJobId = finalRetry.Id;
                await FinishJob(db, finalRetryJobId, SyncJobState.Partial);
            }

            // A retry child on a non-manifest source makes the ownership closure ambiguous.
            var outsiderKey = "qc-sl1815-unmanifested-" + Guid.NewGuid().ToString("N");
            int outsiderSourceId;
            int outsiderJobId;
            await using (var db = new CompareShopDbContext(options))
            {
                var outsider = new DataSource(outsiderKey, "Unmanifested source", "/qc-sl1815/laptop", null, DateTime.UtcNow);
                db.DataSources.Add(outsider);
                await db.SaveChangesAsync();
                outsiderSourceId = outsider.Id;
                var outsiderJob = new SyncJob(outsiderSourceId, DateTime.UtcNow, finalRetryJobId);
                db.SyncJobs.Add(outsiderJob);
                await db.SaveChangesAsync();
                outsiderJobId = outsiderJob.Id;
                await FinishJob(db, outsiderJobId, SyncJobState.Success);
            }
            var countsWithOutsider = await FixtureCounts(services);

            var ownershipFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                ["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Contains("non-fixture DataSource", ownershipFailure.Message);
            Assert.Equal(countsWithOutsider, await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(options))
            {
                Assert.True(await db.Products.AnyAsync(x => x.Slug == "qc-sl1815-existing-product"));
                Assert.True(await db.SyncJobs.AnyAsync(x => x.Id == rootJobId));
                var latestSeed = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                using var json = JsonDocument.Parse(latestSeed.AfterJson!);
                Assert.True(!json.RootElement.TryGetProperty("SyncJobIds", out var jobIds) || jobIds.ValueKind == JsonValueKind.Null);
                await db.SyncJobs.Where(x => x.Id == outsiderJobId).ExecuteDeleteAsync();
                await db.DataSources.Where(x => x.Id == outsiderSourceId).ExecuteDeleteAsync();
            }

            long malformedManifestAuditId;
            await using (var db = new CompareShopDbContext(options))
            {
                var latestSeed = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                var malformed = JsonNode.Parse(latestSeed.AfterJson!)!.AsObject();
                malformed["SyncJobIds"] = JsonSerializer.SerializeToNode(new[] { rootJobId, retryJobId, finalRetryJobId, int.MaxValue });
                var row = new AdminAuditLog
                {
                    ActorSubject = "QC-SL1815", Action = "fixture.manifest.seeded", ResourceType = "QC-SL1815-MatchingFixture",
                    ResourceId = "QC-SL1815", CreatedAtUtc = DateTime.UtcNow, AfterJson = malformed.ToJsonString()
                };
                db.AdminAuditLogs.Add(row);
                await db.SaveChangesAsync();
                malformedManifestAuditId = row.Id;
            }
            var manifestFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                ["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Contains("manifest does not match", manifestFailure.Message);
            await using (var db = new CompareShopDbContext(options))
                await db.AdminAuditLogs.Where(x => x.Id == malformedManifestAuditId).ExecuteDeleteAsync();

            long emptyManifestAuditId;
            var countsBeforeEmptyManifestCleanup = await FixtureCounts(services);
            await using (var db = new CompareShopDbContext(options))
            {
                var latestSeed = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                var explicitlyEmpty = JsonNode.Parse(latestSeed.AfterJson!)!.AsObject();
                explicitlyEmpty["SyncJobIds"] = JsonSerializer.SerializeToNode(Array.Empty<int>());
                var row = new AdminAuditLog
                {
                    ActorSubject = "QC-SL1815", Action = "fixture.manifest.seeded", ResourceType = "QC-SL1815-MatchingFixture",
                    ResourceId = "QC-SL1815", CreatedAtUtc = DateTime.UtcNow, AfterJson = explicitlyEmpty.ToJsonString()
                };
                db.AdminAuditLogs.Add(row);
                await db.SaveChangesAsync();
                emptyManifestAuditId = row.Id;
            }
            var emptyManifestFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                ["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Contains("manifest does not match", emptyManifestFailure.Message);
            Assert.Equal(countsBeforeEmptyManifestCleanup, await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(options))
            {
                Assert.Equal(4, await db.SyncJobs.CountAsync(x => x.Id == rootJobId || x.Id == runningJobId || x.Id == retryJobId || x.Id == finalRetryJobId));
                Assert.True(await db.DataSources.AnyAsync(x => x.Id == sourceId));
                await db.AdminAuditLogs.Where(x => x.Id == emptyManifestAuditId).ExecuteDeleteAsync();
            }

            var rollbackServices = new ServiceCollection()
                .AddDbContext<CompareShopDbContext>(builder => builder.UseSqlServer(connection).AddInterceptors(new FailCleanedManifestInterceptor()))
                .BuildServiceProvider();
            try
            {
                var injectedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => MatchingFixtureCommand.TryRunAsync(
                    ["cleanup-matching-fixtures"], Environment("Development"), config, rollbackServices));
                Assert.Contains("injected cleanup commit failure", injectedFailure.Message);
            }
            finally { await rollbackServices.DisposeAsync(); }

            Assert.Equal(beforeCleanupCounts, await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(options))
            {
                Assert.Equal(4, await db.SyncJobs.CountAsync(x => x.Id == rootJobId || x.Id == runningJobId || x.Id == retryJobId || x.Id == finalRetryJobId));
                Assert.True(await db.DataSources.AnyAsync(x => x.Id == sourceId));
                var latestSeed = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                using var json = JsonDocument.Parse(latestSeed.AfterJson!);
                Assert.True(!json.RootElement.TryGetProperty("SyncJobIds", out var jobIds) || jobIds.ValueKind == JsonValueKind.Null);
            }

            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 1, 0), await FixtureCounts(services));
            await using (var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options))
            {
                Assert.False(await db.Products.AnyAsync(x => x.Id == createdProductId));
                Assert.False(await db.SyncJobs.AnyAsync(x => x.Id == rootJobId || x.Id == runningJobId || x.Id == retryJobId || x.Id == finalRetryJobId));
                Assert.True(await db.SyncJobs.AnyAsync(x => x.Id == unrelatedJobId));
                Assert.True(await db.AdminAuditLogs.AnyAsync(x => x.Action == "create_from_matching" && x.AfterJson!.Contains($"\"productId\":{createdProductId}")));
                var cleanupManifest = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
                    .OrderByDescending(x => x.Id).FirstAsync();
                using var cleanupJson = JsonDocument.Parse(cleanupManifest.AfterJson!);
                Assert.Equal(new[] { rootJobId, runningJobId, retryJobId, finalRetryJobId }.Order(), cleanupJson.RootElement.GetProperty("SyncJobIds").EnumerateArray().Select(x => x.GetInt32()).Order());
                db.ProductMatchings.Remove(unowned);
                await db.SaveChangesAsync();
            }
            Assert.True(await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services));
            Assert.Equal((0, 0, 0, 0, 0, 0, 0), await FixtureCounts(services));
        }
        finally
        {
            try { await MatchingFixtureCommand.TryRunAsync(["cleanup-matching-fixtures"], Environment("Development"), config, services); }
            catch (InvalidOperationException cleanupFailure) { Console.Error.WriteLine($"Fixture test cleanup deferred: {cleanupFailure.Message}"); }
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

    private static async Task<JsonDocument> ReadLatestManifest(DbContextOptions<CompareShopDbContext> options)
    {
        await using var db = new CompareShopDbContext(options);
        var json = await db.AdminAuditLogs.Where(x => x.ActorSubject == "QC-SL1815" && x.ResourceType == "QC-SL1815-MatchingFixture")
            .OrderByDescending(x => x.Id).Select(x => x.AfterJson).FirstAsync();
        return JsonDocument.Parse(json!);
    }

    private static async Task FinishJob(CompareShopDbContext db, int jobId, SyncJobState finalState)
    {
        var job = await db.SyncJobs.SingleAsync(x => x.Id == jobId);
        var now = DateTime.UtcNow;
        if (job.State == SyncJobState.Queued)
            Assert.True(job.Start(now, "fixture-test-" + Guid.NewGuid().ToString("N"), now.AddMinutes(SyncJob.LeaseDurationMinutes)));
        else
            Assert.Equal(SyncJobState.Running, job.State);
        job.Finish(finalState, now.AddSeconds(1), 0, 0, 0);
        await db.SaveChangesAsync();
    }

    private sealed class FailCleanedManifestInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AdminAuditLog>().Any(x => x.State == EntityState.Added && x.Entity.Action == "fixture.manifest.cleaned") == true)
                throw new InvalidOperationException("injected cleanup commit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompareShopAPI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
