using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Api.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class IngestionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("AuthORIZATION: Bearer supersecret", "supersecret")]
    [InlineData("https://user:pass@shop.example/private?token=secret", "token=secret")]
    [InlineData("http://10.0.0.5/private", "10.0.0.5")]
    [InlineData("connection string: Server=db.internal;User ID=api;Password=secret", "Password=secret")]
    [InlineData("navigation failed at https://admin.localhost/catalog", "admin.localhost")]
    [InlineData("navigation failed at https://admin.localhost./catalog", "admin.localhost")]
    [InlineData("navigation failed at https://api.service.home.arpa/catalog", "api.service.home.arpa")]
    [InlineData("navigation failed at https://preview.example/catalog", "preview.example")]
    public void LogSanitizerRedactsCredentialsAndSensitiveUrlParts(string input, string forbidden)
    {
        Assert.DoesNotContain(forbidden, SyncJobLogSanitizer.Clean(input, 2000), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogSanitizerOmitsHtmlAndBoundsText()
    {
        Assert.Equal("[html omitted]", SyncJobLogSanitizer.Clean("<html><body>private response</body></html>", 2000));
        Assert.Equal("12345", SyncJobLogSanitizer.Clean("123456789", 5));
        var entry = new SyncJobLogEntry(1, Now, new string('L', 30), new string('S', 120), new string('M', 2500), new string('E', 250));
        Assert.Equal(16, entry.Level.Length);
        Assert.Equal(80, entry.Step.Length);
        Assert.Equal(2000, entry.Message.Length);
        Assert.Equal(200, entry.ExceptionType!.Length);
    }

    [Fact]
    public async Task SearchValidatesAndClampsQueryAndPageSize()
    {
        var jobs = new FakeJobs();
        var service = new IngestionAdminService(new FakePermissions { Allowed = true }, jobs);
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await service.Search("admin", 2, 500, "BHX", "Failed,Partial", from, null, CancellationToken.None);
        Assert.Equal(100, jobs.LastQuery!.PageSize);
        Assert.Equal(new[] { SyncJobState.Failed, SyncJobState.Partial }, jobs.LastQuery.Statuses);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Search("admin", 1, 25, null, "7", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Search("admin", 1, 25, null, null, from.AddDays(1), from, CancellationToken.None));
    }

    [Fact]
    public async Task LogsRequireExistingJobAndCapPageSize()
    {
        var jobs = new FakeJobs { Exists = true };
        var service = new IngestionAdminService(new FakePermissions { Allowed = true }, jobs);
        await service.Logs("admin", 12, 1, 900, CancellationToken.None);
        Assert.Equal(500, jobs.LogPageSize);
        jobs.Exists = false;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Logs("admin", 13, 1, 100, CancellationToken.None));
    }

    [Fact]
    public async Task SyncJobsRouteKeepsLegacyArrayAndUsesPagedEnvelopeForQueryParameters()
    {
        var jobs = new FakeJobs();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISyncJobStore>(jobs);
        builder.Services.AddSingleton<IDataSourceStore>(new FakeDataSources());
        var permissions = new FakePermissions { Allowed = true };
        builder.Services.AddSingleton<IAdminPermissionReader>(permissions);
        builder.Services.AddScoped<IngestionAdminService>();
        builder.Services.AddScoped<DataSourceAdminService>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin")], "test"));
            await next();
        });
        app.MapGroup("/admin").MapSyncJobEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var legacy = JsonDocument.Parse(await (await client.GetAsync("/admin/sync-jobs")).Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, legacy.RootElement.ValueKind);
        using var paged = JsonDocument.Parse(await (await client.GetAsync("/admin/sync-jobs?page=1&pageSize=1&status=Failed&status=Partial")).Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, paged.RootElement.ValueKind);
        Assert.Equal(1, paged.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(new[] { SyncJobState.Failed, SyncJobState.Partial }, jobs.LastQuery!.Statuses);
        jobs.Exists = true;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/sync-jobs/12/logs?page=1&pageSize=600")).StatusCode);
        Assert.Equal(500, jobs.LogPageSize);
        jobs.Exists = false;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/sync-jobs/999/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/sync-jobs?status=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/sync-jobs/999/logs?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/sync-jobs/999/retry", null)).StatusCode);
        jobs.Retry = new SyncJobSummary(13, 1, "BHX", SyncJobState.Queued, Now, null, null, 0, 0, 0, null, null, 12);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/admin/sync-jobs/12/retry", null)).StatusCode);

        permissions.Allowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/sync-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/sync-jobs/999/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/admin/sync-jobs/12/retry", null)).StatusCode);
    }

    [Fact]
    public void CoordinatorResolvesFromDependencyInjectionWithMultipleReaders()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISyncJobStore>(new FakeJobs());
        services.AddSingleton<ISourceCatalogReader, FakeReader>();
        services.AddSingleton<ISourceCatalogReader, FakeReader>();
        services.AddSingleton<IPriceHistoryRecorder, FakeHistory>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IngestionCoordinator>();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IngestionCoordinator>());
    }

    [Fact]
    public async Task ReaderDefaultStageCallbackKeepsLegacyReadersCompatible()
    {
        ISourceCatalogReader reader = new LegacyReader();
        var stages = new List<string>();
        await reader.ReadAsync("source", "/category", 42, (stage, _) => { stages.Add(stage); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { "catalog_read" }, stages);
    }

    [Fact]
    public async Task CoordinatorImportsAndCompletesPartialJob()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now), Counts = new(1, 1) };
        var reader = new FakeReader { Rows = [new ScrapedOffer("sku", "item", null, 12000, "https://bhx.vn/item")] };
        var history = new FakeHistory();

        await new IngestionCoordinator(jobs, [reader], history, TimeProvider.System).RunDueSources(CancellationToken.None);

        Assert.Equal(SyncJobState.Partial, jobs.CompletedState);
        Assert.Equal(1, jobs.Seen);
        Assert.Equal(1, jobs.Imported);
        Assert.Equal(1, jobs.Pending);
        Assert.Equal(jobs.Claimed!.Id, reader.JobId);
        Assert.True(history.Called);
        Assert.True(jobs.FullSnapshot);
    }

    [Fact]
    public async Task CoordinatorCapturesMilestonesAndKeepsRunningWhenLogStoreFails()
    {
        var rows = new[] { new ScrapedOffer("sku", "item", null, 12000, "https://bhx.vn/item") };
        var successfulLogs = new RecordingLogWriter();
        var successfulJobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        await new IngestionCoordinator(successfulJobs, [new FakeReader { Rows = rows }], new FakeHistory(), TimeProvider.System, logs: successfulLogs).RunDueSources(CancellationToken.None);
        Assert.Equal(SyncJobState.Success, successfulJobs.CompletedState);
        Assert.Contains(successfulLogs.Steps, step => step == "reserved");
        Assert.Contains(successfulLogs.Steps, step => step == "validation");
        Assert.Contains(successfulLogs.Steps, step => step == "price_history");
        Assert.Contains(successfulLogs.Steps, step => step == "completed");

        var failedLogsJobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        await new IngestionCoordinator(failedLogsJobs, [new FakeReader { Rows = rows }], new FakeHistory(), TimeProvider.System, logs: new RecordingLogWriter { FailWrites = true }).RunDueSources(CancellationToken.None);
        Assert.Equal(SyncJobState.Success, failedLogsJobs.CompletedState);
    }

    [Fact]
    public async Task CoordinatorLogsReaderFailureStageAndStepDurations()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        var reader = new FakeReader
        {
            ReportedStages = ["reader_robots", "page_navigation"],
            Failure = new TimeoutException("navigation timed out")
        };
        var logs = new RecordingLogWriter();

        await new IngestionCoordinator(jobs, [reader], new FakeHistory(), TimeProvider.System, logs: logs).RunDueSources(CancellationToken.None);

        var error = Assert.Single(logs.Entries, entry => entry.Level == "Error");
        Assert.Equal("page_navigation", error.Step);
        Assert.NotNull(error.DurationMs);
        Assert.Contains(logs.Entries, entry => entry.Step == "reader_robots" && entry.Level == "Info" && entry.DurationMs is not null);
    }

    [Fact]
    public async Task CoordinatorPropagatesCancellationFromLogStoreWhenStopping()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IngestionCoordinator(jobs, [new FakeReader()], new FakeHistory(), TimeProvider.System, logs: new RecordingLogWriter { CancelWrites = true }).RunDueSources(cancellation.Token));
    }

    [Fact]
    public async Task CoordinatorSkipsClaimedWorkAndFailsUnavailableOrEmptySources()
    {
        var skipped = new FakeJobs { Sources = [Source()], Claimed = null };
        await new IngestionCoordinator(skipped, [new FakeReader()], new FakeHistory(), TimeProvider.System).RunDueSources(CancellationToken.None);
        Assert.Null(skipped.CompletedState);

        var unavailable = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now), Usable = false };
        await new IngestionCoordinator(unavailable, [new FakeReader()], new FakeHistory(), TimeProvider.System).RunDueSources(CancellationToken.None);
        Assert.Equal("shop_unavailable", unavailable.ErrorCode);

        var empty = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        await new IngestionCoordinator(empty, [new FakeReader()], new FakeHistory(), TimeProvider.System).RunDueSources(CancellationToken.None);
        Assert.Equal("source_failure", empty.ErrorCode);
    }

    [Fact]
    public async Task CoordinatorRecordsHistoryFailuresAndPropagatesCancellation()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        var reader = new FakeReader { Rows = [new ScrapedOffer("sku", "item", null, 12000, "https://bhx.vn/item")] };
        await new IngestionCoordinator(jobs, [reader], new FakeHistory { Failure = new InvalidOperationException() }, TimeProvider.System).RunDueSources(CancellationToken.None);
        Assert.Equal("source_failure", jobs.ErrorCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        reader.Failure = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IngestionCoordinator(new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) }, [reader], new FakeHistory(), TimeProvider.System).RunDueSources(cancellation.Token));
    }

    [Fact]
    public async Task CoordinatorFailsAReaderThatDoesNotCompleteBeforeItsExecutionDeadline()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        var pendingRead = new TaskCompletionSource<IReadOnlyList<ScrapedOffer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader { PendingRead = pendingRead.Task };
        var coordinator = new IngestionCoordinator(jobs, [reader], new FakeHistory(), TimeProvider.System, TimeSpan.FromMilliseconds(10));

        await coordinator.RunDueSources(CancellationToken.None);

        Assert.Equal(SyncJobState.Failed, jobs.CompletedState);
        Assert.Equal("execution_timeout", jobs.ErrorCode);
        Assert.Equal("Catalog source exceeded its execution time limit.", jobs.ErrorSummary);
        pendingRead.SetResult([]);
        await pendingRead.Task;
    }

    [Fact]
    public async Task CoordinatorPausesSourceOnBlockedOrChallengedAccessWithSanitizedError()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        var reader = new FakeReader { Failure = new SourceAccessBlockedException() };
        var logs = new RecordingLogWriter();

        await new IngestionCoordinator(jobs, [reader], new FakeHistory(), TimeProvider.System, logs: logs).RunDueSources(CancellationToken.None);

        Assert.Equal(SyncJobState.Failed, jobs.CompletedState);
        Assert.Equal("source_blocked", jobs.ErrorCode);
        Assert.Equal(SourceAccessBlockedException.SanitizedSummary, jobs.ErrorSummary);
        Assert.True(jobs.SourcePaused);
        Assert.Equal(typeof(SourceAccessBlockedException).FullName, Assert.Single(logs.Entries, entry => entry.Step == "browser_start" && entry.Level == "Error").ExceptionType);
    }

    [Fact]
    public async Task CoordinatorPersistsSafeRedirectDenialReasonAndStatus()
    {
        var jobs = new FakeJobs { Sources = [Source()], Claimed = new SyncJob(1, Now) };
        var reader = new FakeReader { Failure = new SourceRedirectDeniedException(302) };

        await new IngestionCoordinator(jobs, [reader], new FakeHistory(), TimeProvider.System).RunDueSources(CancellationToken.None);

        Assert.Equal(SyncJobState.Failed, jobs.CompletedState);
        Assert.Equal("redirect_target_denied_302", jobs.ErrorCode);
        Assert.Equal(SourceRedirectDeniedException.SanitizedSummary, jobs.ErrorSummary);
        Assert.False(jobs.SourcePaused);
    }

    [Fact]
    public async Task AdminServiceEnforcesPermissionsAndReturnsJobsOrRetry()
    {
        var jobs = new FakeJobs();
        var permissions = new FakePermissions();
        var service = new IngestionAdminService(permissions, jobs);

        await Assert.ThrowsAsync<IngestionForbiddenException>(() => service.List(" ", CancellationToken.None));
        await Assert.ThrowsAsync<IngestionForbiddenException>(() => service.List("user", CancellationToken.None));
        permissions.Allowed = true;
        Assert.Empty(await service.List("user", CancellationToken.None));
        permissions.Allowed = false;
        await Assert.ThrowsAsync<IngestionForbiddenException>(() => service.Retry("user", 1, CancellationToken.None));
        permissions.Allowed = true;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Retry("user", 1, CancellationToken.None));
        jobs.Retry = new SyncJobSummary(2, 1, "BHX", SyncJobState.Queued, Now, null, null, 0, 0, 0, null, null, 1);
        Assert.Equal(2, (await service.Retry("user", 1, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task DataSourceAdminServiceRequiresReadPermissionAndReturnsProjectedSources()
    {
        var created = Now;
        var lastCompleted = Now.AddDays(1);
        var nextAttempt = Now.AddDays(2);
        var expected = new DataSourceSummary(7, "bhx", "Bách Hóa Xanh", "/bia", null, DataSourceState.Active, 72, created, lastCompleted, nextAttempt);
        var store = new FakeDataSources { Sources = [expected] };
        var permissions = new FakePermissions { Allowed = true };
        var service = new DataSourceAdminService(permissions, store, TimeProvider.System);

        var result = await service.List("admin", CancellationToken.None);

        Assert.Equal("sync_jobs.read", permissions.LastPermission);
        Assert.Equal(expected, Assert.Single(result));
        Assert.Null(result[0].PartnerShopId);
        permissions.Allowed = false;
        await Assert.ThrowsAsync<IngestionForbiddenException>(() => service.List("admin", CancellationToken.None));
        await Assert.ThrowsAsync<IngestionForbiddenException>(() => service.List(" ", CancellationToken.None));
        permissions.Allowed = true;
        store.Sources = [];
        Assert.Empty(await service.List("admin", CancellationToken.None));
    }

    private static DataSource Source() => new("bhx", "BHX", "/bia", null, Now);

    private sealed class FakeJobs : ISyncJobStore
    {
        public bool Exists { get; set; }
        public SyncJobQuery? LastQuery { get; private set; }
        public int LogPageSize { get; private set; }
        public IReadOnlyList<DataSource> Sources { get; init; } = [];
        public SyncJob? Claimed { get; init; }
        public bool Usable { get; init; } = true;
        public ImportCounts Counts { get; init; } = new(0, 0);
        public SyncJobState? CompletedState { get; private set; }
        public int Seen { get; private set; }
        public int Imported { get; private set; }
        public int Pending { get; private set; }
        public string? ErrorCode { get; private set; }
        public string? ErrorSummary { get; private set; }
        public bool SourcePaused { get; private set; }
        public bool FullSnapshot { get; private set; }
        public SyncJobSummary? Retry { get; set; }
        public Task<IReadOnlyList<DataSource>> DueSources(DateTime nowUtc, CancellationToken ct) => Task.FromResult(Sources);
        public Task<SyncJob?> TryClaim(int sourceId, DateTime nowUtc, CancellationToken ct) => Task.FromResult(Claimed);
        public Task<bool> ShopIsUsable(int sourceId, CancellationToken ct) => Task.FromResult(Usable);
        public Task<ImportCounts> Import(int sourceId, IReadOnlyList<ScrapedOffer> rows, bool fullSnapshot, CancellationToken ct) { FullSnapshot = fullSnapshot; return Task.FromResult(Counts); }
        public Task Complete(int sourceId, SyncJob job, SyncJobState state, DateTime finishedUtc, int seen, int imported, int pending, string? errorCode, string? errorSummary, CancellationToken ct, bool pauseSource = false) { CompletedState = state; Seen = seen; Imported = imported; Pending = pending; ErrorCode = errorCode; ErrorSummary = errorSummary; SourcePaused = pauseSource; return Task.CompletedTask; }
        public Task<IReadOnlyList<SyncJobSummary>> ListJobs(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncJobSummary>>([]);
        public Task<SyncJobPage<SyncJobSummary>> SearchJobs(SyncJobQuery query, CancellationToken ct) { LastQuery = query; return Task.FromResult(new SyncJobPage<SyncJobSummary>([], query.Page, query.PageSize, 0)); }
        public Task<SyncJobPage<SyncJobLog>> JobLogs(int jobId, int page, int pageSize, CancellationToken ct) { LogPageSize = pageSize; return Task.FromResult(new SyncJobPage<SyncJobLog>([], page, pageSize, 0)); }
        public Task<bool> JobExists(int jobId, CancellationToken ct) => Task.FromResult(Exists);
        public Task<SyncJobSummary?> QueueRetry(int jobId, CancellationToken ct) => Task.FromResult(Retry);
    }

    private sealed class FakeDataSources : IDataSourceStore
    {
        public IReadOnlyList<DataSourceSummary> Sources { get; set; } = [];
        public Task<IReadOnlyList<DataSourceSummary>> ListDataSources(CancellationToken ct) => Task.FromResult(Sources);
        public Task<DataSourceSummary?> Configure(int id, DataSourceConfigurationInput input, string actor, CancellationToken ct) => Task.FromResult(Sources.SingleOrDefault(source => source.Id == id));
        public Task<ManualSyncResult> QueueManualSync(int id, DateTime queuedAtUtc, CancellationToken ct) => Task.FromResult(Sources.Any(source => source.Id == id)
            ? new ManualSyncResult(ManualSyncResultKind.Unavailable)
            : new ManualSyncResult(ManualSyncResultKind.Missing));
    }

    private sealed class FakeReader : ISourceCatalogReader
    {
        public IReadOnlyList<ScrapedOffer> Rows { get; init; } = [];
        public IReadOnlyList<string> ReportedStages { get; init; } = [];
        public Task<IReadOnlyList<ScrapedOffer>>? PendingRead { get; init; }
        public int? JobId { get; private set; }
        public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct)
        {
            JobId = jobId;
            return PendingRead ?? ReadValidatedBiaAsync(ct);
        }
        public Exception? Failure { get; set; }
        public async Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, Func<string, CancellationToken, Task> stageChanged, CancellationToken ct)
        {
            JobId = jobId;
            foreach (var stage in ReportedStages) await stageChanged(stage, ct);
            return await (PendingRead ?? ReadValidatedBiaAsync(ct));
        }
        public Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct) => Failure is null ? Task.FromResult(Rows) : Task.FromException<IReadOnlyList<ScrapedOffer>>(Failure);
    }

    private sealed class LegacyReader : ISourceCatalogReader
    {
        public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScrapedOffer>>([]);
    }

    private sealed class FakeHistory : IPriceHistoryRecorder
    {
        public bool Called { get; private set; }
        public Exception? Failure { get; init; }
        public Task RecordForChangedMinimums(CancellationToken ct) { Called = true; return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
    }

    private sealed class FakePermissions : IAdminPermissionReader
    {
        public bool Allowed { get; set; }
        public string? LastPermission { get; private set; }
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) { LastPermission = permission; return Task.FromResult(Allowed); }
    }
    private sealed class RecordingLogWriter : ISyncJobLogWriter
    {
        public bool FailWrites { get; init; }
        public bool CancelWrites { get; init; }
        public List<string> Steps { get; } = [];
        public List<(string Level, string Step, string? ExceptionType, int? DurationMs)> Entries { get; } = [];
        public Task Write(int jobId, string level, string step, string message, CancellationToken ct, Exception? exception = null, int? httpStatusCode = null, int? durationMs = null)
        {
            if (FailWrites) throw new InvalidOperationException("log store unavailable");
            if (CancelWrites) throw new OperationCanceledException(ct);
            Steps.Add(step);
            Entries.Add((level, step, exception?.GetType().FullName, durationMs));
            return Task.CompletedTask;
        }
        public Task<int> DeleteExpired(DateTime cutoffUtc, int take, CancellationToken ct) => Task.FromResult(0);
    }
}
