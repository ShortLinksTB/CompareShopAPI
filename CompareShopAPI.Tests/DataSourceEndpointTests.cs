using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class DataSourceEndpointTests(SqlServerTestFixture fixture)
{
    [Fact]
    public async Task ConfigureAuditsAtomicallyAndManualSyncQueuesOnlyOneActiveJob()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CompareShopDataSourceMutations_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var actor = $"source-configurer-{Guid.NewGuid():N}";
        var viewerSubject = $"source-viewer-{Guid.NewGuid():N}";
        var sourceKey = $"qc-sl1815-sl2330-{Guid.NewGuid():N}";
        var createdAt = DateTime.UtcNow;
        try
        {
            int sourceId;
            int shopId;
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                var shop = new PartnerShop { Slug = $"sl-2330-shop-{Guid.NewGuid():N}", Name = "SL-2330 Test Shop" };
                var source = new DataSource(sourceKey, "SL-2330 Test Source", "/qc-sl1815/laptop", null, createdAt);
                var user = new User(actor, createdAt);
                var viewer = new User(viewerSubject, createdAt);
                db.PartnerShops.Add(shop);
                db.DataSources.Add(source);
                db.Users.AddRange(user, viewer);
                await db.SaveChangesAsync();
                shopId = shop.Id;
                sourceId = source.Id;
                db.AdminUserRoles.AddRange(new AdminUserRole { UserId = user.Id, RoleId = 2 }, new AdminUserRole { UserId = viewer.Id, RoleId = 4 });
                await db.SaveChangesAsync();
            }

            await using var app = new ApiFactory(settings: new Dictionary<string, string?> { ["ConnectionStrings:CompareShopDb"] = connection }, signing: true);
            using var client = app.CreateClient();
            var roles = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(roles, subject: viewerSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new { state = "Paused", intervalHours = 24, partnerShopId = (int?)null })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/admin/data-sources/{sourceId}/sync", null)).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(roles, subject: actor));
            Assert.Equal(HttpStatusCode.NotFound, (await PatchJsonAsync(client, $"/admin/data-sources/{sourceId + 10000}", new { state = "Paused", intervalHours = 24, partnerShopId = (int?)null })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/admin/data-sources/{sourceId + 10000}/sync", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new { state = "Unknown", intervalHours = 24, partnerShopId = (int?)null })).StatusCode);

            var configure = await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new
            {
                state = "Paused",
                intervalHours = 24,
                partnerShopId = shopId
            });
            Assert.Equal(HttpStatusCode.OK, configure.StatusCode);
            using (var body = JsonDocument.Parse(await configure.Content.ReadAsStringAsync()))
            {
                Assert.Equal((int)DataSourceState.Paused, body.RootElement.GetProperty("state").GetInt32());
                Assert.Equal(24, body.RootElement.GetProperty("intervalHours").GetInt32());
                Assert.Equal(shopId, body.RootElement.GetProperty("partnerShopId").GetInt32());
                Assert.Equal("SL-2330 Test Shop", body.RootElement.GetProperty("partnerShopDisplayName").GetString());
            }

            var invalidShop = await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new
            {
                state = "Paused",
                intervalHours = 24,
                partnerShopId = shopId + 10000
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidShop.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/admin/data-sources/{sourceId}/sync", null)).StatusCode);

            var activate = await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new
            {
                state = "Active",
                intervalHours = 720,
                partnerShopId = shopId
            });
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            var concurrentRequests = await Task.WhenAll(
                client.PostAsync($"/admin/data-sources/{sourceId}/sync", null),
                client.PostAsync($"/admin/data-sources/{sourceId}/sync", null));
            Assert.Single(concurrentRequests, response => response.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(concurrentRequests, response => response.StatusCode == HttpStatusCode.Conflict);
            var queued = concurrentRequests.Single(response => response.StatusCode == HttpStatusCode.Accepted);
            using (var job = JsonDocument.Parse(await queued.Content.ReadAsStringAsync()))
            {
                Assert.Equal(sourceId, job.RootElement.GetProperty("dataSourceId").GetInt32());
                Assert.Equal((int)SyncJobState.Queued, job.RootElement.GetProperty("state").GetInt32());
            }
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/admin/data-sources/{sourceId}/sync", null)).StatusCode);

            await using (var db = new CompareShopDbContext(options))
            {
                var source = await db.DataSources.SingleAsync(item => item.Id == sourceId);
                source.MarkError();
                await db.SaveChangesAsync();
            }
            var reactivateErrorSource = await PatchJsonAsync(client, $"/admin/data-sources/{sourceId}", new
            {
                state = "Active",
                intervalHours = 24,
                partnerShopId = shopId
            });
            Assert.Equal(HttpStatusCode.BadRequest, reactivateErrorSource.StatusCode);

            await using (var db = new CompareShopDbContext(options))
            {
                var shop = await db.PartnerShops.SingleAsync(item => item.Id == shopId);
                db.PartnerShops.Remove(shop);
                await db.SaveChangesAsync();
            }
            using (var sourcesResponse = await client.GetAsync("/admin/data-sources"))
            {
                Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
                using var sources = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync());
                var source = Assert.Single(sources.RootElement.EnumerateArray(), item => item.GetProperty("id").GetInt32() == sourceId);
                Assert.Equal(JsonValueKind.Null, source.GetProperty("partnerShopId").ValueKind);
                Assert.Equal(JsonValueKind.Null, source.GetProperty("partnerShopDisplayName").ValueKind);
            }

            await using (var db = new CompareShopDbContext(options))
            {
                var audits = await db.AdminAuditLogs.Where(log => log.ActorSubject == actor && log.ResourceType == "data_source" && log.ResourceId == sourceId.ToString()).ToListAsync();
                Assert.Equal(2, audits.Count);
                Assert.Contains("intervalHours", audits[0].BeforeJson);
                Assert.DoesNotContain("categoryPath", audits[0].BeforeJson, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, await db.SyncJobs.CountAsync(job => job.DataSourceId == sourceId));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<HttpResponseMessage> PatchJsonAsync<T>(HttpClient client, string uri, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task DataSourcesEndpointReturnsRegistryAndKeepsSyncJobHistorySeparate()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = $"CompareShopDataSources_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var operatorSubject = $"source-reader-{Guid.NewGuid():N}";
        var viewerSubject = $"source-viewer-{Guid.NewGuid():N}";
        var sourceKey = $"sl-1452-test-{Guid.NewGuid():N}";
        var createdAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            int sourceId;
            int jobId;
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [DataSources] ([Key], [DisplayName], [CategoryPath], [PartnerShopId], [State], [IntervalHours], [CreatedAtUtc], [LastCompletedAtUtc], [NextAttemptAtUtc])
                    VALUES ({sourceKey}, N'SL-1452 Test Source', N'/test', NULL, N'Active', 72, {createdAt}, {createdAt.AddHours(3)}, {createdAt.AddHours(10)});
                    """);
                sourceId = await db.DataSources.Where(source => source.Key == sourceKey).Select(source => source.Id).SingleAsync();
                var job = new SyncJob(sourceId, createdAt.AddHours(5));
                db.SyncJobs.Add(job);

                var operatorUser = new User(operatorSubject, createdAt);
                var viewerUser = new User(viewerSubject, createdAt);
                db.Users.AddRange(operatorUser, viewerUser);
                await db.SaveChangesAsync();
                db.AdminUserRoles.AddRange(
                    new CompareShopAPI.Domain.Admin.AdminUserRole { UserId = operatorUser.Id, RoleId = 2 },
                    new CompareShopAPI.Domain.Admin.AdminUserRole { UserId = viewerUser.Id, RoleId = 4 });
                await db.SaveChangesAsync();
                jobId = job.Id;
            }

            var settings = new Dictionary<string, string?> { ["ConnectionStrings:CompareShopDb"] = connection };
            await using var app = new ApiFactory(settings: settings, signing: true);
            using var client = app.CreateClient();
            var roles = "{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}";

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/data-sources")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(roles, subject: viewerSubject));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/data-sources")).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(roles, subject: operatorSubject));
            var sourcesResponse = await client.GetAsync("/admin/data-sources");
            Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
            using (var sources = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync()))
            {
                var source = Assert.Single(sources.RootElement.EnumerateArray()
                    .Where(item => item.GetProperty("key").GetString() == sourceKey));
                Assert.Equal(sourceId, source.GetProperty("id").GetInt32());
                Assert.Equal(sourceKey, source.GetProperty("key").GetString());
                Assert.Equal("SL-1452 Test Source", source.GetProperty("displayName").GetString());
                Assert.Equal("/test", source.GetProperty("categoryPath").GetString());
                Assert.Equal(JsonValueKind.Null, source.GetProperty("partnerShopId").ValueKind);
                Assert.Equal(JsonValueKind.Null, source.GetProperty("partnerShopDisplayName").ValueKind);
                Assert.Equal((int)DataSourceState.Active, source.GetProperty("state").GetInt32());
                Assert.Equal(72, source.GetProperty("intervalHours").GetInt32());
                Assert.Equal(createdAt, source.GetProperty("createdAtUtc").GetDateTime());
                Assert.Equal(createdAt.AddHours(3), source.GetProperty("lastCompletedAtUtc").GetDateTime());
                Assert.Equal(createdAt.AddHours(10), source.GetProperty("nextAttemptAtUtc").GetDateTime());
            }

            var jobsResponse = await client.GetAsync("/admin/sync-jobs");
            Assert.Equal(HttpStatusCode.OK, jobsResponse.StatusCode);
            using (var jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync()))
            {
                var job = Assert.Single(jobs.RootElement.EnumerateArray());
                Assert.Equal(jobId, job.GetProperty("id").GetInt32());
                Assert.Equal(sourceId, job.GetProperty("dataSourceId").GetInt32());
                Assert.Equal("SL-1452 Test Source", job.GetProperty("dataSource").GetString());
            }

            await using (var db = new CompareShopDbContext(options))
            {
                db.SyncJobs.RemoveRange(db.SyncJobs);
                db.DataSources.RemoveRange(db.DataSources);
                await db.SaveChangesAsync();
            }
            using var emptyResponse = await client.GetAsync("/admin/data-sources");
            Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
            using var empty = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
            Assert.Empty(empty.RootElement.EnumerateArray());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
