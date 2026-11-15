extern alias Workers;

using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Infrastructure.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class SyncJobLogStoreTests
{
    [Fact]
    public async Task SearchFiltersBeforePagingAndOrdersByStartThenId()
    {
        var options = Options();
        int sourceId;
        await using (var db = new CompareShopDbContext(options))
        {
            var source = new DataSource(DataSource.BhxKey, "Bách Hóa Xanh", "/bia", null, DateTime.UtcNow);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            sourceId = source.Id;
            AddFinishedJob(db, sourceId, SyncJobState.Failed, DateTime.UtcNow.AddMinutes(-1));
            AddFinishedJob(db, sourceId, SyncJobState.Partial, DateTime.UtcNow);
            AddFinishedJob(db, sourceId, SyncJobState.Success, DateTime.UtcNow.AddMinutes(1));
            await db.SaveChangesAsync();
        }

        await using var queryDb = new CompareShopDbContext(options);
        var page = await new EfSyncJobStore(queryDb).SearchJobs(new(1, 1, "hÓa", [SyncJobState.Failed, SyncJobState.Partial], null, null), CancellationToken.None);
        Assert.Equal(2, page.TotalItems);
        var onlyItem = Assert.Single(page.Items);
        Assert.Equal(SyncJobState.Partial, onlyItem.State);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task LogsAreIsolatedOrderedAndOldJobsReturnAnEmptyPage()
    {
        var options = Options();
        int firstJobId;
        int secondJobId;
        var occurred = DateTime.UtcNow;
        await using (var db = new CompareShopDbContext(options))
        {
            var source = new DataSource(DataSource.BhxKey, "BHX", "/bia", null, occurred);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            var first = new SyncJob(source.Id, occurred);
            var second = new SyncJob(source.Id, occurred);
            db.SyncJobs.AddRange(first, second);
            await db.SaveChangesAsync();
            firstJobId = first.Id;
            secondJobId = second.Id;
            db.SyncJobLogEntries.AddRange(
                new SyncJobLogEntry(first.Id, occurred.AddMinutes(2), "Info", "import", "later"),
                new SyncJobLogEntry(first.Id, occurred.AddMinutes(1), "Error", "read", "earlier"));
            await db.SaveChangesAsync();
        }

        await using var readDb = new CompareShopDbContext(options);
        var store = new EfSyncJobStore(readDb);
        Assert.True(await store.JobExists(firstJobId, CancellationToken.None));
        Assert.False(await store.JobExists(int.MaxValue, CancellationToken.None));
        var firstPage = await store.JobLogs(firstJobId, 1, 1, CancellationToken.None);
        var secondJobLogs = await store.JobLogs(secondJobId, 1, 100, CancellationToken.None);
        Assert.Equal(2, firstPage.TotalItems);
        Assert.Equal("earlier", Assert.Single(firstPage.Items).Message);
        Assert.Empty(secondJobLogs.Items);
    }

    [Fact]
    public async Task WriterSanitizesMessagesAndExceptionDetails()
    {
        var options = Options();
        int jobId;
        await using (var db = new CompareShopDbContext(options))
        {
            var source = new DataSource(DataSource.BhxKey, "BHX", "/bia", null, DateTime.UtcNow);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            var job = new SyncJob(source.Id, DateTime.UtcNow);
            db.SyncJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await using var writeDb = new CompareShopDbContext(options);
        var writer = new EfSyncJobLogWriter(new EfSyncJobStore(writeDb));
        await writer.Write(jobId, "Error", "read", "Authorization: Bearer header-secret", CancellationToken.None, new InvalidOperationException("Token=exception-secret"));
        var log = await writeDb.SyncJobLogEntries.SingleAsync();
        Assert.DoesNotContain("header-secret", log.Message);
        Assert.DoesNotContain("exception-secret", log.Message);
        Assert.Equal(nameof(InvalidOperationException), log.ExceptionType);
    }

    [Fact]
    public async Task RetentionWorkerRequestsThirtyDayCutoffAndCapsBatchAt500()
    {
        var recorder = new RetentionRecorder();
        var services = new ServiceCollection().AddSingleton<ISyncJobLogWriter>(recorder).BuildServiceProvider();
        var worker = new Workers::CompareShopAPI.Workers.Ingestion.SyncJobLogRetentionWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<Workers::CompareShopAPI.Workers.Ingestion.SyncJobLogRetentionWorker>.Instance);
        var before = DateTime.UtcNow.AddDays(-30);
        Assert.Equal(12, await worker.CleanupExpiredOnce(CancellationToken.None));
        Assert.Equal(500, recorder.Take);
        Assert.InRange(recorder.CutoffUtc, before.AddSeconds(-1), DateTime.UtcNow.AddDays(-30).AddSeconds(1));
    }

    [Fact]
    public async Task WriterEnforcesPerJobCapAndCleanupDeletesAtMost500ExpiredRows()
    {
        var options = Options();
        int jobId;
        var now = DateTime.UtcNow;
        await using (var db = new CompareShopDbContext(options))
        {
            var source = new DataSource(DataSource.BhxKey, "BHX", "/bia", null, now);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            var job = new SyncJob(source.Id, now);
            db.SyncJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            db.SyncJobLogEntries.AddRange(Enumerable.Range(0, 499).Select(i => new SyncJobLogEntry(jobId, now.AddDays(-40).AddSeconds(i), "Info", "read", "entry")));
            await db.SaveChangesAsync();
        }

        await using var writeDb = new CompareShopDbContext(options);
        var store = new EfSyncJobStore(writeDb);
        await store.WriteLog(jobId, "Error", "read", "should be capped", null, null, null, CancellationToken.None);
        Assert.Equal(500, await writeDb.SyncJobLogEntries.CountAsync());
        Assert.Equal("retention", (await writeDb.SyncJobLogEntries.OrderByDescending(x => x.Id).FirstAsync()).Step);
        var writer = new EfSyncJobLogWriter(store);
        Assert.Equal(499, await writer.DeleteExpired(now.AddDays(-30), 500, CancellationToken.None));
        Assert.Equal("retention", (await writeDb.SyncJobLogEntries.SingleAsync()).Step);
    }

    private static DbContextOptions<CompareShopDbContext> Options() => new DbContextOptionsBuilder<CompareShopDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

    private sealed class RetentionRecorder : ISyncJobLogWriter
    {
        public DateTime CutoffUtc { get; private set; }
        public int Take { get; private set; }
        public Task Write(int jobId, string level, string step, string message, CancellationToken ct, Exception? exception = null, int? httpStatusCode = null, int? durationMs = null) => Task.CompletedTask;
        public Task<int> DeleteExpired(DateTime cutoffUtc, int take, CancellationToken ct) { CutoffUtc = cutoffUtc; Take = take; return Task.FromResult(12); }
    }

    private static void AddFinishedJob(CompareShopDbContext db, int sourceId, SyncJobState state, DateTime started)
    {
        var job = new SyncJob(sourceId, started.AddMinutes(-1));
        job.Start(started, Guid.NewGuid().ToString("N"), started.AddMinutes(20));
        job.Finish(state, started.AddSeconds(10), 1, 1, 0);
        db.SyncJobs.Add(job);
    }
}
