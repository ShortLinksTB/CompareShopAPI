using System.Globalization;

using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class BhxIngestionWorker(IServiceScopeFactory scopes, ILogger<BhxIngestionWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded startup jitter prevents synchronized replicas; DB lease remains authoritative.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 60)), stoppingToken);
        using var timer = new PeriodicTimer(ResolvePollInterval(configuration));
        do
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<IngestionCoordinator>().RunDueSources(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "BHX ingestion poll failed; retrying on the next bounded poll"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static TimeSpan ResolvePollInterval(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredMinutes = configuration["Ingestion:PollIntervalMinutes"];
        if (configuredMinutes is null)
            return TimeSpan.FromMinutes(60);

        if (!int.TryParse(configuredMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
            throw new InvalidOperationException("Ingestion:PollIntervalMinutes must be a positive integer.");

        return TimeSpan.FromMinutes(minutes);
    }
}
