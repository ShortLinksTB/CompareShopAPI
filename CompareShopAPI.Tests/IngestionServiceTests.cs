using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class IngestionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

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

        await new IngestionCoordinator(jobs, [reader], new FakeHistory(), TimeProvider.System).RunDueSources(CancellationToken.None);

        Assert.Equal(SyncJobState.Failed, jobs.CompletedState);
        Assert.Equal("source_blocked", jobs.ErrorCode);
        Assert.Equal(SourceAccessBlockedException.SanitizedSummary, jobs.ErrorSummary);
        Assert.True(jobs.SourcePaused);
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

    private static DataSource Source() => new("bhx", "BHX", "/bia", null, Now);

    private sealed class FakeJobs : ISyncJobStore
    {
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
        public Task<SyncJobSummary?> QueueRetry(int jobId, CancellationToken ct) => Task.FromResult(Retry);
    }

    private sealed class FakeReader : ISourceCatalogReader
    {
        public IReadOnlyList<ScrapedOffer> Rows { get; init; } = [];
        public Task<IReadOnlyList<ScrapedOffer>>? PendingRead { get; init; }
        public int? JobId { get; private set; }
        public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct)
        {
            JobId = jobId;
            return PendingRead ?? ReadValidatedBiaAsync(ct);
        }
        public Exception? Failure { get; set; }
        public Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct) => Failure is null ? Task.FromResult(Rows) : Task.FromException<IReadOnlyList<ScrapedOffer>>(Failure);
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
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(Allowed);
    }
}
