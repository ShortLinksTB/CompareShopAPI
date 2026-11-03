using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
