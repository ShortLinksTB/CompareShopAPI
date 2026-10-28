extern alias Workers;
using System.Net;
using CompareShopAPI.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Worker = Workers::CompareShopAPI.Workers.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CatalogReadDiagnosticsTests
{
    [Fact]
    public async Task Cellphones_logs_safe_failure_and_preserves_exception()
    {
        var failure = new InvalidOperationException("body <html> cookie=secret token=private", new Exception("credential"));
        failure.Data["Reason"] = "untrusted body";
        var logger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        var reader = new Worker.CellphoneSPlaywrightCatalogReader(_ => throw failure, logger);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync("cellphones", "/laptop/mac.html", default)));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("ExceptionType=System.InvalidOperationException", entry.Text);
        Assert.Contains("Reason=Unclassified", entry.Text);
        Assert.Contains("ReadValidatedCoreAsync", entry.Text);
        foreach (var secret in new[] { "<html>", "secret", "private", "credential", "untrusted body" }) Assert.DoesNotContain(secret, entry.Text);
        Assert.True(entry.Text.Length < 1500);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Robots_denial_has_fixed_reason_for_both_readers(bool bhx)
    {
        var logger = new Capture<Worker.BhxPlaywrightCatalogReader>();
        var cellLogger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        using var client = new HttpClient(new RobotsHandler("User-agent: *\nDisallow: /\n# token=private"));
        if (bhx)
            await Assert.ThrowsAsync<InvalidDataException>(() => new Worker.BhxPlaywrightCatalogReader(() => client, logger).ReadValidatedBiaAsync(default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => new Worker.CellphoneSPlaywrightCatalogReader(_ => client, cellLogger).ReadAsync("cellphones", "/laptop/mac.html", default));
        var entry = Assert.Single(bhx ? logger.Entries : cellLogger.Entries);
        Assert.Contains("Reason=RobotsPolicyDenied", entry.Text);
        Assert.Contains("ExceptionType=System.IO.InvalidDataException", entry.Text);
        Assert.DoesNotContain("private", entry.Text);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Diagnostics_preserve_success_blocked_and_cancellation_without_warning()
    {
        var logger = new Capture<object>();
        Assert.Equal(42, await Worker.CatalogReadDiagnostics.ReadAsync(logger, () => Task.FromResult(42)));
        await Assert.ThrowsAsync<SourceAccessBlockedException>(() => Worker.CatalogReadDiagnostics.ReadAsync<int>(logger, () => throw new SourceAccessBlockedException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Worker.CatalogReadDiagnostics.ReadAsync<int>(logger, () => throw new OperationCanceledException()));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Production_DI_resolves_both_readers_with_logging()
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddSingleton<ISourceCatalogReader, Worker.BhxPlaywrightCatalogReader>()
            .AddSingleton<ISourceCatalogReader, Worker.CellphoneSPlaywrightCatalogReader>().BuildServiceProvider();
        Assert.Equal(2, provider.GetServices<ISourceCatalogReader>().Count());
        _ = new Worker.CellphoneSPlaywrightCatalogReader();
        _ = new Worker.CellphoneSPlaywrightCatalogReader(_ => new HttpClient());
    }

    private sealed class RobotsHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class Capture<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, exception), exception));
    }
}
