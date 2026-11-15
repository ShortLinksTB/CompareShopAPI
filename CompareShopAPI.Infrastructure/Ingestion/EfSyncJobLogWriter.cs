using CompareShopAPI.Application.Ingestion;

namespace CompareShopAPI.Infrastructure.Ingestion;

public sealed class EfSyncJobLogWriter(EfSyncJobStore store) : ISyncJobLogWriter
{
    public Task Write(int jobId, string level, string step, string message, CancellationToken ct, Exception? exception = null, int? httpStatusCode = null, int? durationMs = null) =>
        store.WriteLog(jobId, level, step, SyncJobLogSanitizer.Clean(exception is null ? message : $"{message} ({SyncJobLogSanitizer.Clean(exception.Message, 1000)})", 2000), exception is null ? null : SyncJobLogSanitizer.Clean(exception.GetType().Name, 200), httpStatusCode, durationMs, ct);
    public Task<int> DeleteExpired(DateTime cutoffUtc, int take, CancellationToken ct) => store.DeleteExpiredLogs(cutoffUtc, take, ct);
}
