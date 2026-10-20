using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Application.Admin;

namespace CompareShopAPI.Application.Ingestion;

public interface ISourceCatalogReader { Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct); }
public interface ISyncJobStore
{
    Task<IReadOnlyList<DataSource>> DueSources(DateTime nowUtc, CancellationToken ct);
    Task<SyncJob?> TryClaim(int sourceId, DateTime nowUtc, CancellationToken ct);
    Task<bool> ShopIsUsable(int sourceId, CancellationToken ct);
    Task<ImportCounts> Import(int sourceId, IReadOnlyList<ScrapedOffer> rows, bool fullSnapshot, CancellationToken ct);
    Task Complete(int sourceId, SyncJob job, SyncJobState state, DateTime finishedUtc, int seen, int imported, int pending, string? errorCode, string? errorSummary, CancellationToken ct, bool pauseSource = false);
    Task<IReadOnlyList<SyncJobSummary>> ListJobs(CancellationToken ct);
    Task<SyncJobSummary?> QueueRetry(int jobId, CancellationToken ct);
}
public sealed record SyncJobSummary(int Id, int DataSourceId, string DataSource, SyncJobState State, DateTime QueuedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, int SeenCount, int ImportedCount, int PendingCount, string? ErrorCode, string? ErrorSummary, int? RetryOfJobId);
public interface IPriceHistoryRecorder { Task RecordForChangedMinimums(CancellationToken ct); }
public sealed record ImportCounts(int Imported, int Pending);
public sealed class SourceAccessBlockedException : Exception
{
    public const string SanitizedSummary = "BHX access was blocked or challenged; source paused.";
    public SourceAccessBlockedException() : base(SanitizedSummary) { }
}
public sealed class SourceRedirectDeniedException : Exception
{
    public const string ReasonCode = "redirect_target_denied";
    public const string SanitizedSummary = "BHX redirect destination was denied by the host policy.";
    public string ErrorCode { get; }

    public SourceRedirectDeniedException(int redirectStatusCode) : base(SanitizedSummary)
    {
        ErrorCode = $"{ReasonCode}_{redirectStatusCode}";
    }
}

public sealed class IngestionCoordinator(ISyncJobStore jobs, ISourceCatalogReader source, IPriceHistoryRecorder history, TimeProvider clock)
{
    public async Task RunDueSources(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var dataSource in await jobs.DueSources(now, ct))
        {
            var job = await jobs.TryClaim(dataSource.Id, now, ct);
            if (job is null) continue;
            if (!await jobs.ShopIsUsable(dataSource.Id, ct))
            {
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0, "shop_unavailable", "Partner shop is missing or disabled.", ct);
                continue;
            }
            try
            {
                // Keep active work shorter than the persisted lease so an expired claim is always safe to reclaim.
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execution.CancelAfter(TimeSpan.FromMinutes(SyncJob.LeaseDurationMinutes - 10));
                var rows = await source.ReadValidatedBiaAsync(execution.Token);
                if (rows.Count == 0) throw new InvalidDataException("Source returned no validated products.");
                var result = await jobs.Import(dataSource.Id, rows, true, execution.Token);
                await history.RecordForChangedMinimums(execution.Token);
                await jobs.Complete(dataSource.Id, job, result.Pending > 0 ? SyncJobState.Partial : SyncJobState.Success, clock.GetUtcNow().UtcDateTime, rows.Count, result.Imported, result.Pending, null, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SourceAccessBlockedException)
            {
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0,
                    "source_blocked", SourceAccessBlockedException.SanitizedSummary, ct, pauseSource: true);
            }
            catch (SourceRedirectDeniedException ex)
            {
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0,
                    ex.ErrorCode, SourceRedirectDeniedException.SanitizedSummary, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("BHX source sync failed; job {0} is recorded as failed ({1}).", job.Id, ex.GetType().Name);
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0, "source_failure", "BHX source could not be read or imported.", ct);
            }
        }
    }
}

public sealed class IngestionAdminService(IAdminPermissionReader permissions, ISyncJobStore jobs)
{
    public async Task<IReadOnlyList<SyncJobSummary>> List(string? subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.read", ct)) throw new IngestionForbiddenException();
        return await jobs.ListJobs(ct);
    }
    public async Task<SyncJobSummary> Retry(string? subject, int id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.execute", ct)) throw new IngestionForbiddenException();
        return await jobs.QueueRetry(id, ct) ?? throw new KeyNotFoundException("A failed or partial job was not found.");
    }
}
public sealed class IngestionForbiddenException : Exception { }
