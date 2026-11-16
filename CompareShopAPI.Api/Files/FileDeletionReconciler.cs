using CompareShopAPI.Application.Files;

namespace CompareShopAPI.Api.Files;

public sealed class FileDeletionReconciler(IServiceScopeFactory scopeFactory, ILogger<FileDeletionReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxRetriesPerCycle = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await ReconcilePendingFiles(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "File deletion reconciliation cycle failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ReconcilePendingFiles(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileObjectStorage>();
        IReadOnlyList<(Guid Id, string ObjectKey)> pending;
        try { pending = await files.DeletingFiles(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not load pending file deletions for reconciliation.");
            return;
        }

        foreach (var item in pending)
        {
            var completed = false;
            for (var retry = 1; retry <= MaxRetriesPerCycle && !completed; retry++)
            {
                try
                {
                    try { await storage.Delete(item.ObjectKey, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException && IsObjectMissing(ex)) { /* Idempotent delete: object already absent. */ }
                    completed = await files.FinalizeDelete(item.Id, ct);
                    if (!completed) logger.LogWarning("File deletion finalization remains pending for {FileId}", item.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "File deletion reconciliation attempt {Attempt} failed for {FileId}", retry, item.Id);
                }
            }
            if (!completed) logger.LogError("File deletion exhausted {RetryCount} retries in this reconciliation cycle for {FileId}; tombstone remains pending.", MaxRetriesPerCycle, item.Id);
        }
    }

    private static bool IsObjectMissing(Exception exception) => exception.Message.Contains("NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("NoSuchObject", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("ObjectNotFound", StringComparison.OrdinalIgnoreCase);
}
