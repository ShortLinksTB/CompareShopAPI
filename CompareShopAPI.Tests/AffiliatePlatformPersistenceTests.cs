using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class AffiliatePlatformPersistenceTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task Crud_preview_commit_bulk_delete_and_reload_follow_contract()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"AffiliatePlatform_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            var writerSubject = $"affiliate-writer-{Guid.NewGuid():N}";
            var viewerSubject = $"affiliate-viewer-{Guid.NewGuid():N}";
            await using (var setup = new CompareShopDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var writer = new User(writerSubject, DateTime.UtcNow);
                var viewer = new User(viewerSubject, DateTime.UtcNow);
                var noPermissionRole = new AdminRole { Name = $"Affiliate Viewer {Guid.NewGuid():N}" };
                setup.AddRange(writer, viewer, noPermissionRole);
                await setup.SaveChangesAsync();
                setup.AdminUserRoles.AddRange(
                    new AdminUserRole { UserId = writer.Id, RoleId = 1 },
                    new AdminUserRole { UserId = viewer.Id, RoleId = noPermissionRole.Id });
                await setup.SaveChangesAsync();
            }

            var settings = new Dictionary<string, string?> { ["ConnectionStrings:CompareShopDb"] = connection };
            await using (var app = new ApiFactory(settings: settings, signing: true))
            {
                using var client = app.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/affiliate-platforms")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: viewerSubject));
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/affiliate-platforms")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: writerSubject));
                using var response = await client.PostAsJsonAsync("/admin/affiliate-platforms", new { name = "Endpoint platform", homeUrl = "https://endpoint.example", notes = (string?)null });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var endpointRecord = await response.Content.ReadFromJsonAsync<AffiliatePlatformRecord>();
                Assert.NotNull(endpointRecord);
                using var update = await client.PutAsJsonAsync($"/admin/affiliate-platforms/{endpointRecord.Id}", new { name = "Updated endpoint platform", homeUrl = (string?)null, notes = "updated" });
                Assert.Equal(HttpStatusCode.OK, update.StatusCode);
                var listed = await client.GetFromJsonAsync<List<AffiliatePlatformRecord>>("/admin/affiliate-platforms");
                Assert.Contains(listed!, x => x.Id == endpointRecord.Id && x.Name == "Updated endpoint platform");
                using var duplicate = await client.PostAsJsonAsync("/admin/affiliate-platforms", new { name = " UPDATED ENDPOINT PLATFORM ", homeUrl = (string?)null, notes = (string?)null });
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
                using var invalid = await client.PostAsJsonAsync("/admin/affiliate-platforms", new { name = "   ", homeUrl = "/relative", notes = (string?)null });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                var importRows = new[]
                {
                    new { name = "Imported endpoint platform", homeUrl = (string?)"https://import.example", notes = (string?)"from import" },
                    new { name = "   ", homeUrl = (string?)null, notes = (string?)null }
                };
                using var previewResponse = await client.PostAsJsonAsync("/admin/affiliate-platforms/import/preview", importRows);
                Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
                var preview = await previewResponse.Content.ReadFromJsonAsync<AffiliatePlatformImportPreview>();
                Assert.Equal(1, preview!.CreateCount);
                Assert.Equal(1, preview.Errors.Single().Index);
                var afterPreview = await client.GetFromJsonAsync<List<AffiliatePlatformRecord>>("/admin/affiliate-platforms") ?? [];
                Assert.Single(afterPreview);
                using var commitResponse = await client.PostAsJsonAsync("/admin/affiliate-platforms/import/commit", importRows);
                Assert.Equal(HttpStatusCode.OK, commitResponse.StatusCode);
                var importResult = await commitResponse.Content.ReadFromJsonAsync<AffiliatePlatformImportResult>();
                Assert.Equal((1, 1), (importResult!.CreatedCount, importResult.FailedCount));
                listed = await client.GetFromJsonAsync<List<AffiliatePlatformRecord>>("/admin/affiliate-platforms");
                var imported = Assert.Single(listed!, x => x.Name == "Imported endpoint platform");
                using var bulkDeleteResponse = await client.PostAsJsonAsync("/admin/affiliate-platforms/bulk-delete", new { ids = new[] { imported.Id, imported.Id, int.MaxValue } });
                Assert.Equal(HttpStatusCode.OK, bulkDeleteResponse.StatusCode);
                var deleteResults = await bulkDeleteResponse.Content.ReadFromJsonAsync<List<AffiliatePlatformDeleteResult>>();
                Assert.Equal(new[] { "deleted", "not_found" }, deleteResults!.Select(x => x.Status));
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/affiliate-platforms/{endpointRecord.Id}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/admin/affiliate-platforms/{endpointRecord.Id}")).StatusCode);
            }

            AffiliatePlatformRecord saved;
            await using (var db = new CompareShopDbContext(options))
            {
                var store = new EfAffiliatePlatformStore(db);
                var service = new AffiliatePlatformService(store, new AllowAllPermissions(), TimeProvider.System);
                saved = await service.Create("admin", new("  Partner  One ", "https://partner.example/path", new string('n', 5000)), default);
                Assert.Equal("Partner  One", saved.Name);
                await Assert.ThrowsAsync<AffiliatePlatformDuplicateException>(() =>
                    service.Create("admin", new("partner  one", null, null), default));

                var preview = await service.Preview("admin", [
                    new("PARTNER  ONE", "https://updated.example", null),
                    new("New partner", null, "added"),
                    new("   ", null, null)
                ], default);
                Assert.Equal(1, preview.CreateCount);
                Assert.Equal(1, preview.UpdateCount);
                Assert.Equal(2, preview.Errors.Single().Index);
                Assert.Single(await db.AffiliatePlatforms.ToListAsync());

                var committed = await service.Commit("admin", [
                    new("PARTNER  ONE", "https://updated.example", null),
                    new("New partner", null, "added"),
                    new("   ", null, null)
                ], default);
                Assert.Equal(1, committed.CreatedCount);
                Assert.Equal(1, committed.UpdatedCount);
                Assert.Equal(1, committed.FailedCount);
            }

            await using (var reloaded = new CompareShopDbContext(options))
            {
                var rows = await new EfAffiliatePlatformStore(reloaded).List(default);
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, x => x.Id == saved.Id && x.Name == "PARTNER  ONE" && x.HomeUrl == "https://updated.example");
                Assert.Contains(rows, x => x.Name == "New partner" && x.Notes == "added");

                var store = new EfAffiliatePlatformStore(reloaded);
                var service = new AffiliatePlatformService(store, new AllowAllPermissions(), TimeProvider.System);
                var results = await service.BulkDelete("admin", [saved.Id, saved.Id, int.MaxValue], default);
                Assert.Equal(new[] { "deleted", "not_found" }, results.Select(x => x.Status));
                Assert.False(await service.Delete("admin", int.MaxValue, default));
            }

            await using var verified = new CompareShopDbContext(options);
            Assert.Single(await verified.AffiliatePlatforms.ToListAsync());
            Assert.True(await verified.AffiliatePlatforms.AnyAsync(x => x.Name == "New partner"));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class AllowAllPermissions : IAdminPermissionReader
    {
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(true);
    }
}
