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
            var category = new Category { Slug = "matching", Name = "Matching" };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
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
            var entry = json.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == first.Id);
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
            Assert.Equal(1d, candidate.GetProperty("similarityScore").GetDouble());
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
            await service.ApproveMatching("matching-reader", second.Id, best.Id, default);
            counter.ProductReads = 0;
            Assert.Empty(await service.MatchingList("matching-reader", default));
            Assert.Equal(0, counter.ProductReads);
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
