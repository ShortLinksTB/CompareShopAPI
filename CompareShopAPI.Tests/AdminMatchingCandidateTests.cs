using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AdminMatchingCandidateTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task AttachedMatchingsAreScopedAuthorizedAndUnlinkRetainsTheApprovedRecord()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
            { InitialCatalog = $"AttachedMatchings_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var productId = 0;
        var matchingId = 0;
        var sourceMissingId = 0;
        var sourceId = 0;
        var subject = $"matching-access-{Guid.NewGuid():N}";
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                var category = await CategoryTestData.AddLeafAsync(db, "attached-matchings", "Attached matchings");
                var product = new Product { Slug = "attached-product", Name = "Attached product", CategoryId = category.Id };
                var otherProduct = new Product { Slug = "other-attached-product", Name = "Other attached product", CategoryId = category.Id };
                db.Products.AddRange(product, otherProduct);
                await db.SaveChangesAsync();
                productId = product.Id;

                var source = await db.DataSources.FirstAsync(x => x.Key == DataSource.BhxKey);
                sourceId = source.Id;
                var matching = ProductMatching.ForSourceItem("Attached item", "Brand", DateTime.UtcNow, source.Id, "sku-123");
                Assert.True(matching.TryApprove(product.Id));
                var sourceMissing = new ProductMatching("Attached item without source", null, DateTime.UtcNow);
                Assert.True(sourceMissing.TryApprove(product.Id));
                var other = new ProductMatching("Other product item", null, DateTime.UtcNow);
                Assert.True(other.TryApprove(otherProduct.Id));
                db.ProductMatchings.AddRange(matching, sourceMissing, other);

                var reader = new User(subject, DateTime.UtcNow);
                var denied = new User($"matching-denied-{Guid.NewGuid():N}", DateTime.UtcNow);
                db.Users.AddRange(reader, denied);
                var role = new AdminRole { Name = $"matching-read-{Guid.NewGuid():N}" };
                db.AdminRoles.Add(role);
                await db.SaveChangesAsync();
                var readPermission = await db.AdminPermissions.SingleAsync(x => x.Key == AdminPermissions.MatchingRead);
                db.AdminRolePermissions.Add(new() { RoleId = role.Id, PermissionId = readPermission.Id });
                db.AdminUserRoles.Add(new() { UserId = reader.Id, RoleId = role.Id });
                await db.SaveChangesAsync();
                matchingId = matching.Id;
                sourceMissingId = sourceMissing.Id;
            }

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/admin/products/{productId}/matchings")).StatusCode);
            const string roles = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(roles, subject: $"missing-{subject}"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/admin/products/{productId}/matchings")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(roles, subject: subject));
            using (var response = await client.GetAsync($"/admin/products/{productId}/matchings"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var items = json.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2, items.Length);
                var item = Assert.Single(items, x => x.GetProperty("id").GetInt32() == matchingId);
                Assert.Equal(matchingId, item.GetProperty("id").GetInt32());
                Assert.Equal("Attached item", item.GetProperty("name").GetString());
                Assert.Equal("Brand", item.GetProperty("brand").GetString());
                Assert.Equal(productId, item.GetProperty("productId").GetInt32());
                Assert.True(item.GetProperty("isApproved").GetBoolean());
                Assert.Equal("sku-123", item.GetProperty("externalSku").GetString());
                Assert.Equal(sourceId, item.GetProperty("dataSourceId").GetInt32());
                Assert.Equal("Bách Hóa Xanh", item.GetProperty("dataSourceDisplayName").GetString());
                var withoutSource = Assert.Single(items, x => x.GetProperty("id").GetInt32() == sourceMissingId);
                Assert.Equal(JsonValueKind.Null, withoutSource.GetProperty("dataSourceId").ValueKind);
                Assert.Equal(JsonValueKind.Null, withoutSource.GetProperty("dataSourceDisplayName").ValueKind);
            }
            Assert.Equal("[]", await (await client.GetAsync("/admin/products/999999/matchings")).Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/admin/matching/{matchingId}")).StatusCode);

            await using (var grant = new CompareShopDbContext(options))
            {
                var user = await grant.Users.SingleAsync(x => x.KeycloakSubject == subject);
                var role = await grant.AdminUserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId).SingleAsync();
                var permission = await grant.AdminPermissions.SingleAsync(x => x.Key == AdminPermissions.MatchingApprove);
                grant.AdminRolePermissions.Add(new() { RoleId = role, PermissionId = permission.Id });
                await grant.SaveChangesAsync();
            }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/matching/{matchingId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/admin/matching/999999")).StatusCode);
            using (var queue = JsonDocument.Parse(await (await client.GetAsync("/admin/matching")).Content.ReadAsStringAsync()))
                Assert.Empty(queue.RootElement.GetProperty("items").EnumerateArray());
            await using (var verify = new CompareShopDbContext(options))
            {
                var retained = await verify.ProductMatchings.AsNoTracking().SingleAsync(x => x.Id == matchingId);
                Assert.Null(retained.ProductId);
                Assert.True(retained.IsApproved);
                Assert.Equal("Attached item", retained.Name);
                Assert.Equal("Brand", retained.Brand);
                Assert.Equal("sku-123", retained.ExternalSku);
                Assert.Equal(sourceId, retained.DataSourceId);
                var remaining = Assert.Single(await new EfAdminData(verify, TimeProvider.System).MatchingsForProduct(productId, default));
                Assert.Equal(sourceMissingId, remaining.Id);
                Assert.Null(remaining.DataSourceId);
                Assert.Null(remaining.DataSourceDisplayName);
                Assert.Single(await verify.AdminAuditLogs.AsNoTracking().Where(x => x.Action == "unlink" && x.ResourceId == matchingId.ToString()).ToListAsync());
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task GetIsAdditiveReadOnlyAuthorizedAndOnlyExplicitPostApproves()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
            { InitialCatalog = $"MatchingCandidates_{Guid.NewGuid():N}" }.ConnectionString;
        var counter = new ProductQueryCounter();
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).AddInterceptors(counter).Options;
        await using var db = new CompareShopDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            var reader = new User("matching-reader", DateTime.UtcNow);
            var denied = new User("matching-denied", DateTime.UtcNow);
            db.Users.AddRange(reader, denied);
            var category = await CategoryTestData.AddLeafAsync(db, "matching", "Matching");
            // A principal with matching.read alone must be able to get suggestions.
            var role = new AdminRole { Name = "Matching reader" };
            db.AdminRoles.Add(role);
            await db.SaveChangesAsync();
            var permission = await db.AdminPermissions.SingleAsync(x => x.Key == AdminPermissions.MatchingRead);
            db.AdminRolePermissions.Add(new() { RoleId = role.Id, PermissionId = permission.Id });
            db.AdminUserRoles.Add(new() { UserId = reader.Id, RoleId = role.Id });
            var source = await db.DataSources.FirstAsync(x => x.Key == DataSource.BhxKey);
            var mapping = new SourceItemMapping(source.Id, "matching-sku", "Mác Air", "Apple");
            db.SourceItemMappings.Add(mapping);
            var first = ProductMatching.ForSourceItem("Mác Air", "Apple", DateTime.UtcNow, source.Id, "matching-sku");
            var second = new ProductMatching("Other pending", null, DateTime.UtcNow);
            db.ProductMatchings.AddRange(first, second);
            await db.SaveChangesAsync();
            var service = new AdminService(new EfAdminData(db, TimeProvider.System));
            Assert.All(await service.MatchingList("matching-reader", default), x => Assert.Empty(x.Candidates));
            Assert.Empty(await new EfAdminData(db, TimeProvider.System).MatchingCandidateProducts([], default));

            var best = new Product { Slug = "best", Name = "Mac Air", Brand = "Apple", CategoryId = category.Id };
            var fallback = new Product { Slug = "fallback", Name = "Unrelated", Brand = null, CategoryId = category.Id };
            var inactive = new Product { Slug = "inactive", Name = "Mac Air", Brand = "Apple", CategoryId = category.Id, IsActive = false };
            db.Products.AddRange(best, fallback, inactive);
            await db.SaveChangesAsync();
            counter.ProductReads = 0;
            var ranked = await service.MatchingList("matching-reader", default);
            Assert.Equal(1, counter.ProductReads);
            Assert.Equal(2, ranked.Count);
            Assert.Equal(new[] { best.Id, fallback.Id }, ranked[0].Candidates.Select(x => x.ProductId));
            Assert.All(ranked, x => Assert.DoesNotContain(x.Candidates, c => c.ProductId == inactive.Id));

            await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/matching")).StatusCode);
            const string roles = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(roles, subject: "matching-denied"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/matching")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(roles, subject: "matching-reader"));
            var response = await client.GetAsync("/admin/matching");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entry = json.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == first.Id);
            Assert.Equal("Mác Air", entry.GetProperty("name").GetString());
            Assert.Equal("Apple", entry.GetProperty("brand").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("productId").ValueKind);
            Assert.False(entry.GetProperty("isApproved").GetBoolean());
            Assert.True(entry.TryGetProperty("createdAtUtc", out _));
            Assert.True(entry.TryGetProperty("externalSku", out _));
            Assert.True(entry.TryGetProperty("dataSourceId", out _));
            var candidate = entry.GetProperty("candidates")[0];
            Assert.Equal(best.Id, candidate.GetProperty("productId").GetInt32());
            Assert.Equal("Mac Air", candidate.GetProperty("name").GetString());
            Assert.Equal("Apple", candidate.GetProperty("brand").GetString());
            Assert.True(candidate.GetProperty("score").GetDouble() >= 0d);
            Assert.True(candidate.TryGetProperty("level", out _));
            Assert.True(candidate.TryGetProperty("differences", out _));
            Assert.Equal(2, await db.ProductMatchings.CountAsync(x => !x.IsApproved && x.ProductId == null));
            Assert.Null((await db.SourceItemMappings.AsNoTracking().SingleAsync()).ProductId);
            Assert.Empty(await db.AdminAuditLogs.AsNoTracking().ToListAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/admin/matching/{first.Id}/approve", new { productId = best.Id })).StatusCode);
            // Grant approval and deliberately choose a candidate other than rank one.
            var approve = await db.AdminPermissions.SingleAsync(x => x.Key == AdminPermissions.MatchingApprove);
            db.AdminRolePermissions.Add(new() { RoleId = role.Id, PermissionId = approve.Id });
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/admin/matching/{first.Id}/approve", new { productId = fallback.Id })).StatusCode);
            await db.Entry(first).ReloadAsync();
            Assert.True(first.IsApproved);
            Assert.Equal(fallback.Id, first.ProductId);
            Assert.Equal(fallback.Id, (await db.SourceItemMappings.AsNoTracking().SingleAsync()).ProductId);
            Assert.False(second.IsApproved);
            Assert.Single(await db.AdminAuditLogs.AsNoTracking().Where(x => x.Action == "approve" && x.ResourceType == "matching").ToListAsync());
            Assert.Single(await service.MatchingList("matching-reader", default));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/admin/matching/{second.Id}/skip", new { reason = "duplicate", note = "same listing" })).StatusCode);
            using (var skippedQueue = JsonDocument.Parse(await (await client.GetAsync("/admin/matching?status=skipped")).Content.ReadAsStringAsync()))
            {
                Assert.Equal(1, skippedQueue.RootElement.GetProperty("items").GetArrayLength());
                Assert.Equal(1, skippedQueue.RootElement.GetProperty("counts").GetProperty("skipped").GetInt32());
            }
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/admin/matching/{second.Id}/restore", null)).StatusCode);
            using (var restoredQueue = JsonDocument.Parse(await (await client.GetAsync("/admin/matching")).Content.ReadAsStringAsync()))
                Assert.Equal(1, restoredQueue.RootElement.GetProperty("items").GetArrayLength());
            await service.ApproveMatching("matching-reader", second.Id, best.Id, default);
            counter.ProductReads = 0;
            Assert.Empty(await service.MatchingList("matching-reader", default));
            Assert.Equal(0, counter.ProductReads);
            Assert.Equal(1, await db.MatchingDecisions.AsNoTracking().CountAsync(x => x.MatchingId == second.Id && x.Decision == "skipped"));
            Assert.Equal(1, await db.MatchingDecisions.AsNoTracking().CountAsync(x => x.MatchingId == second.Id && x.Decision == "restored"));
            Assert.Equal(1, await db.MatchingDecisions.AsNoTracking().CountAsync(x => x.MatchingId == second.Id && x.Decision == "approved"));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class ProductQueryCounter : DbCommandInterceptor
    {
        public int ProductReads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Products]")) ProductReads++;
            return ValueTask.FromResult(result);
        }
    }
}
