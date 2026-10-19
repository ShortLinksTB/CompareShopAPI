extern alias Workers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerScheduling = Workers::CompareShopAPI.Workers.Ingestion.BhxIngestionWorker;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class BhxIngestionWorkerTests
{
    [Fact]
    public void Poll_interval_defaults_to_sixty_minutes()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(TimeSpan.FromMinutes(60), WorkerScheduling.ResolvePollInterval(configuration));
    }

    [Fact]
    public void Poll_interval_uses_configured_minutes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:PollIntervalMinutes"] = "5"
            })
            .Build();

        var worker = new WorkerScheduling(new UnusedScopeFactory(), NullLogger<WorkerScheduling>.Instance, configuration);

        Assert.Equal(TimeSpan.FromMinutes(5), WorkerScheduling.ResolvePollInterval(configuration));
        Assert.NotNull(worker);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void Poll_interval_rejects_invalid_values(string configuredMinutes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:PollIntervalMinutes"] = configuredMinutes
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => WorkerScheduling.ResolvePollInterval(configuration));
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException();
    }
}
