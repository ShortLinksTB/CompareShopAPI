using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class BhxIngestionWorker(IServiceScopeFactory scopes, ILogger<BhxIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded startup jitter prevents synchronized replicas; DB lease remains authoritative.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 60)), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<IngestionCoordinator>().RunDueSources(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "BHX ingestion poll failed; retrying on the next bounded poll"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
