using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class SyncJobLogRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SyncJobLogRetentionWorker> logger) : BackgroundService
{
    public async Task<int> CleanupExpiredOnce(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISyncJobLogWriter>().DeleteExpired(clock.GetUtcNow().UtcDateTime.AddDays(-30), 500, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await CleanupExpiredOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Sync job log retention cleanup failed; retrying on the next hourly tick"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
