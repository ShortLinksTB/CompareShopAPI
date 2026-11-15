using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Application.Admin;

namespace CompareShopAPI.Application.Ingestion;

public interface ISourceCatalogReader
{
    bool Supports(string sourceKey) => true;
    Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct) => ReadAsync(sourceKey, categoryPath, ct);
    Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, CancellationToken ct) => ReadValidatedBiaAsync(ct);
    Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct) => throw new NotSupportedException("Source reader requires a source-aware request.");
}
public interface ISyncJobStore
{
    Task<IReadOnlyList<DataSource>> DueSources(DateTime nowUtc, CancellationToken ct);
    Task<SyncJob?> TryClaim(int sourceId, DateTime nowUtc, CancellationToken ct);
    Task<bool> ShopIsUsable(int sourceId, CancellationToken ct);
    Task<ImportCounts> Import(int sourceId, IReadOnlyList<ScrapedOffer> rows, bool fullSnapshot, CancellationToken ct);
    Task Complete(int sourceId, SyncJob job, SyncJobState state, DateTime finishedUtc, int seen, int imported, int pending, string? errorCode, string? errorSummary, CancellationToken ct, bool pauseSource = false);
    Task<IReadOnlyList<SyncJobSummary>> ListJobs(CancellationToken ct);
    Task<SyncJobPage<SyncJobSummary>> SearchJobs(SyncJobQuery query, CancellationToken ct);
    Task<SyncJobPage<SyncJobLog>> JobLogs(int jobId, int page, int pageSize, CancellationToken ct);
    Task<bool> JobExists(int jobId, CancellationToken ct);
    Task<SyncJobSummary?> QueueRetry(int jobId, CancellationToken ct);
}
public sealed record SyncJobQuery(int Page, int PageSize, string? Q, IReadOnlyList<SyncJobState> Statuses, DateTime? StartedFrom, DateTime? StartedTo);
public sealed record SyncJobPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems);
public sealed record SyncJobLog(int Id, DateTime OccurredAtUtc, string Level, string Step, string Message, string? ExceptionType, int? HttpStatusCode, int? DurationMs);
public interface ISyncJobLogWriter
{
    Task Write(int jobId, string level, string step, string message, CancellationToken ct, Exception? exception = null, int? httpStatusCode = null, int? durationMs = null);
    Task<int> DeleteExpired(DateTime cutoffUtc, int take, CancellationToken ct);
}
public static class SyncJobLogSanitizer
{
    public static string Clean(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        if (input.Contains("<html", StringComparison.OrdinalIgnoreCase) || input.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)) return "[html omitted]";
        var value = System.Text.RegularExpressions.Regex.Replace(input, @"(?i)(https?://)[^\s/]+(?:/[^\s?#]*)?(?:\?[^\s#]*)?(?:#[^\s]*)?", m => SafeHostOnly(m.Value));
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(connection[\s_-]*string)\s*[:=]\s*[^\r\n]+", "$1=[redacted]");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(authorization|cookie|set-cookie|token|password|secret|proxy-authorization)\s*[:=]\s*(?:Bearer\s+)?[^\s,;]+(?:\s+[^\s,;]+)?", "$1=[redacted]");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(?:[A-Z]:\\|/)(?:[^\s:]+[/\\])+[^\s:]*", "[path]");
        return value[..Math.Min(value.Length, maxLength)];
    }
    private static string SafeHostOnly(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return "[url]";
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || new[] { ".local", ".internal", ".lan", ".corp", ".home", ".intranet", ".test" }.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) || !host.Contains('.') || System.Net.IPAddress.TryParse(host, out _)) return "[url]";
        return $"{uri.Scheme}://{host}/[redacted]";
    }
}
public interface IDataSourceStore
{
    Task<IReadOnlyList<DataSourceSummary>> ListDataSources(CancellationToken ct);
    Task<DataSourceSummary?> Configure(int id, DataSourceConfigurationInput input, string actor, CancellationToken ct);
    Task<ManualSyncResult> QueueManualSync(int id, DateTime queuedAtUtc, CancellationToken ct);
}
public sealed record DataSourceConfigurationInput(DataSourceState State, int IntervalHours, int? PartnerShopId);
public enum ManualSyncResultKind { Queued, Missing, Unavailable }
public sealed record ManualSyncResult(ManualSyncResultKind Kind, SyncJobSummary? Job = null);
public sealed record DataSourceSummary(int Id, string Key, string DisplayName, string CategoryPath, int? PartnerShopId, DataSourceState State, int IntervalHours, DateTime CreatedAtUtc, DateTime? LastCompletedAtUtc, DateTime? NextAttemptAtUtc, string? PartnerShopDisplayName = null);
public sealed record SyncJobSummary(int Id, int DataSourceId, string DataSource, SyncJobState State, DateTime QueuedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, int SeenCount, int ImportedCount, int PendingCount, string? ErrorCode, string? ErrorSummary, int? RetryOfJobId);
public interface IPriceHistoryRecorder { Task RecordForChangedMinimums(CancellationToken ct); }
public sealed record ImportCounts(int Imported, int Pending);
public sealed class SourceAccessBlockedException : Exception
{
    public const string SanitizedSummary = "Catalog source access was blocked or challenged; source paused.";
    public SourceAccessBlockedException() : base(SanitizedSummary) { }
}
public sealed class SourceRedirectDeniedException : Exception
{
    public const string ReasonCode = "redirect_target_denied";
    public const string SanitizedSummary = "Catalog source redirect destination was denied by the host policy.";
    public string ErrorCode { get; }

    public SourceRedirectDeniedException(int redirectStatusCode) : base(SanitizedSummary)
    {
        ErrorCode = $"{ReasonCode}_{redirectStatusCode}";
    }
}

public sealed class IngestionCoordinator(ISyncJobStore jobs, IEnumerable<ISourceCatalogReader> readers, IPriceHistoryRecorder history, TimeProvider clock, TimeSpan? executionTimeout = null, ISyncJobLogWriter? logs = null)
{
    private readonly TimeSpan executionLimit = ResolveExecutionLimit(executionTimeout);

    public async Task RunDueSources(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var dataSource in await jobs.DueSources(now, ct))
        {
            var job = await jobs.TryClaim(dataSource.Id, now, ct);
            if (job is null) continue;
            await Log(job.Id, "Info", "reserved", "Job reserved.", ct);
            if (!await jobs.ShopIsUsable(dataSource.Id, ct))
            {
                await Log(job.Id, "Error", "shop_check", "Partner shop is missing or disabled.", ct);
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0, "shop_unavailable", "Partner shop is missing or disabled.", ct);
                continue;
            }
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
            execution.CancelAfter(executionLimit);
            var failureStep = "browser_start";
            try
            {
                // Keep active work shorter than the persisted lease so an expired claim is always safe to reclaim.
                var sourceKey = dataSource.Key;
                await Log(job.Id, "Info", "browser_start", "Starting source reader.", execution.Token);
                var reader = readers.SingleOrDefault(candidate => candidate.Supports(sourceKey));
                if (reader is null || !DataSource.IsSupportedCategory(sourceKey, dataSource.CategoryPath)) throw new InvalidDataException("No reader supports this source category.");
                // Some browser-driver operations do not accept a CancellationToken. WaitAsync
                // keeps the job bounded even if one of those operations stops responding.
                var rows = await reader.ReadAsync(sourceKey, dataSource.CategoryPath, job.Id, execution.Token)
                    .WaitAsync(execution.Token);
                await Log(job.Id, "Info", "page_read", $"Read {rows.Count} product rows.", execution.Token);
                failureStep = "validation";
                await Log(job.Id, "Info", "validation", $"Validated {rows.Count(row => row.IsValid)} of {rows.Count} product rows.", execution.Token);
                if (rows.Count == 0 || rows.Any(row => !row.IsValid)) throw new InvalidDataException("Source returned no validated products.");
                failureStep = "import";
                var result = await jobs.Import(dataSource.Id, rows, true, execution.Token);
                await Log(job.Id, "Info", "import", $"Imported {result.Imported}; pending {result.Pending}.", execution.Token);
                failureStep = "price_history";
                await history.RecordForChangedMinimums(execution.Token);
                await Log(job.Id, "Info", "price_history", "Price history recorded.", execution.Token);
                await jobs.Complete(dataSource.Id, job, result.Pending > 0 ? SyncJobState.Partial : SyncJobState.Success, clock.GetUtcNow().UtcDateTime, rows.Count, result.Imported, result.Pending, null, null, ct);
                await Log(job.Id, "Info", "completed", "Job completed.", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Catalog source sync timed out; job {0} is recorded as failed.", job.Id);
                await Log(job.Id, "Error", "execution", "Catalog source exceeded its execution time limit.", ct, new TimeoutException("Execution timeout."));
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0,
                    "execution_timeout", "Catalog source exceeded its execution time limit.", ct);
            }
            catch (SourceAccessBlockedException ex)
            {
                await Log(job.Id, "Error", failureStep, SourceAccessBlockedException.SanitizedSummary, ct, ex);
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0,
                    "source_blocked", SourceAccessBlockedException.SanitizedSummary, ct, pauseSource: true);
            }
            catch (SourceRedirectDeniedException ex)
            {
                await Log(job.Id, "Error", failureStep, SourceRedirectDeniedException.SanitizedSummary, ct, ex, int.TryParse(ex.ErrorCode.Split('_').Last(), out var code) ? code : null);
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0,
                    ex.ErrorCode, SourceRedirectDeniedException.SanitizedSummary, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Catalog source sync failed; job {0} is recorded as failed ({1}).", job.Id, ex.GetType().Name);
                await Log(job.Id, "Error", failureStep, ex.Message, ct, ex);
                await jobs.Complete(dataSource.Id, job, SyncJobState.Failed, clock.GetUtcNow().UtcDateTime, 0, 0, 0, "source_failure", "Catalog source could not be read or imported.", ct);
            }
        }
    }

    private async Task Log(int jobId, string level, string step, string message, CancellationToken ct, Exception? exception = null, int? status = null)
    {
        if (logs is null) return;
        try { await logs.Write(jobId, level, step, message, ct, exception, status); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception logError) { System.Diagnostics.Trace.TraceWarning("Sync job log write failed for job {0} ({1}).", jobId, logError.GetType().Name); }
    }

    private static TimeSpan ResolveExecutionLimit(TimeSpan? configured)
    {
        var limit = configured ?? TimeSpan.FromMinutes(SyncJob.LeaseDurationMinutes - 10);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(SyncJob.LeaseDurationMinutes - 10))
            throw new ArgumentOutOfRangeException(nameof(configured), "Catalog execution timeout must be positive and shorter than the sync-job lease.");
        return limit;
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
    public async Task<SyncJobPage<SyncJobSummary>> Search(string? subject, int page, int pageSize, string? q, string? status, DateTime? from, DateTime? to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.read", ct)) throw new IngestionForbiddenException();
        if (page < 1 || pageSize < 1) throw new ArgumentException("Page and pageSize must be positive.");
        if (page > int.MaxValue / Math.Min(pageSize, 100)) throw new ArgumentException("Page is too large.");
        if (from is not null && from.Value.Kind != DateTimeKind.Utc || to is not null && to.Value.Kind != DateTimeKind.Utc || from is not null && to is not null && from.Value > to.Value) throw new ArgumentException("Date range must be UTC and ordered.");
        var statuses = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Enum.TryParse<SyncJobState>(x, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("Invalid sync job status.")).Distinct().ToArray();
        return await jobs.SearchJobs(new(page, Math.Min(pageSize, 100), q, statuses, from, to), ct);
    }
    public async Task<SyncJobPage<SyncJobLog>> Logs(string? subject, int id, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.read", ct)) throw new IngestionForbiddenException();
        if (page < 1 || pageSize < 1) throw new ArgumentException("Page and pageSize must be positive.");
        if (page > int.MaxValue / Math.Min(pageSize, 500)) throw new ArgumentException("Page is too large.");
        if (!await jobs.JobExists(id, ct)) throw new KeyNotFoundException();
        return await jobs.JobLogs(id, page, Math.Min(pageSize, 500), ct);
    }
}
public sealed class DataSourceAdminService(IAdminPermissionReader permissions, IDataSourceStore sources, TimeProvider clock)
{
    public async Task<IReadOnlyList<DataSourceSummary>> List(string? subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.read", ct)) throw new IngestionForbiddenException();
        return await sources.ListDataSources(ct);
    }

    public async Task<DataSourceSummary> Configure(string? subject, int id, DataSourceConfigurationInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.execute", ct)) throw new IngestionForbiddenException();
        if (id <= 0) throw new ArgumentException("Data source ID must be positive.", nameof(id));
        return await sources.Configure(id, input, subject, ct) ?? throw new KeyNotFoundException("Data source was not found.");
    }

    public async Task<SyncJobSummary> QueueSync(string? subject, int id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || !await permissions.HasPermission(subject, "sync_jobs.execute", ct)) throw new IngestionForbiddenException();
        if (id <= 0) throw new ArgumentException("Data source ID must be positive.", nameof(id));
        var result = await sources.QueueManualSync(id, clock.GetUtcNow().UtcDateTime, ct);
        return result.Kind switch
        {
            ManualSyncResultKind.Queued => result.Job!,
            ManualSyncResultKind.Missing => throw new KeyNotFoundException("Data source was not found."),
            _ => throw new DataSourceSyncConflictException()
        };
    }
}
public sealed class IngestionForbiddenException : Exception { }
public sealed class DataSourceSyncConflictException : Exception { }
