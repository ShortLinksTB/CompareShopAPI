extern alias Workers;
using System.Net;
using System.Reflection;
using Microsoft.Playwright;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Job_and_source_are_logged_at_the_failing_robots_stage(bool bhx)
    {
        var failure = new TimeoutException("proxy-password token=private");
        var logger = new Capture<Worker.BhxPlaywrightCatalogReader>();
        var cellLogger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        ISourceCatalogReader reader = bhx
            ? new Worker.BhxPlaywrightCatalogReader(() => throw failure, logger)
            : new Worker.CellphoneSPlaywrightCatalogReader(_ => throw failure, cellLogger);
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() =>
            reader.ReadAsync(bhx ? "bhx" : "cellphones", bhx ? "/bia" : "/laptop/mac.html", 28, default)));
        var entry = Assert.Single(bhx ? logger.Entries : cellLogger.Entries);
        Assert.Contains("Source=" + (bhx ? "bhx" : "cellphones"), entry.Text);
        Assert.Contains("JobId=28", entry.Text);
        Assert.Contains("Stage=Robots", entry.Text);
        Assert.DoesNotContain("private", entry.Text);
        Assert.DoesNotContain("proxy-password", entry.Text);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Cellphones_denied_url_diagnostic_correlates_job_and_redacts_sensitive_query_values()
    {
        var logger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        var reader = new Worker.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(), logger);
        var context = new Worker.CatalogReadContext("cellphones", 31) { Stage = Worker.CatalogReadStage.ProductFields };
        var expectedUrl = new Uri("https://www.cellphones.com.vn/macbook-neo.html?product_id=125876&session=card-secret");

        Assert.False(reader.TryValidateDetailUrl(context, expectedUrl,
            "https://www.cellphones.com.vn/macbook-pro.html?product_id=125876&token=redirect-secret#private", out _));
        Assert.True(reader.TryValidateDetailUrl(context, expectedUrl,
            "https://www.cellphones.com.vn/macbook-neo.html?product_id=125876&token=not-logged", out var finalUri));
        Assert.Equal(expectedUrl.AbsolutePath, finalUri.AbsolutePath);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("Source=cellphones", entry.Text);
        Assert.Contains("JobId=31", entry.Text);
        Assert.Contains("Stage=ProductFields", entry.Text);
        Assert.Contains("CardHref=https://www.cellphones.com.vn/macbook-neo.html", entry.Text);
        Assert.Contains("CardHrefPath=/macbook-neo.html", entry.Text);
        Assert.Contains("FinalRequestUri=https://www.cellphones.com.vn/macbook-pro.html", entry.Text);
        Assert.Contains("FinalRequestPath=/macbook-pro.html", entry.Text);
        Assert.DoesNotContain("card-secret", entry.Text);
        Assert.DoesNotContain("redirect-secret", entry.Text);
        Assert.DoesNotContain("private", entry.Text);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Cellphones_route_aborts_and_logs_denied_main_frame_redirect_then_continues_allowed_request()
    {
        var logger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        var reader = new Worker.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(), logger);
        var context = new Worker.CatalogReadContext("cellphones", 32) { Stage = Worker.CatalogReadStage.Navigation };
        var frame = Stub.Create<IFrame>((method, _) => method == "get_ParentFrame" ? null : throw new NotSupportedException(method));
        var requestUrl = "https://evil.test/macbook-neo.html?product_id=125876&token=route-secret";
        var request = Stub.Create<IRequest>((method, _) => method switch
        {
            "get_Url" => requestUrl,
            "get_IsNavigationRequest" => true,
            "get_Frame" => frame,
            _ => throw new NotSupportedException(method)
        });
        var aborts = 0;
        var continues = 0;
        var route = Stub.Create<IRoute>((method, _) => method switch
        {
            "get_Request" => request,
            "AbortAsync" => CountAbort(),
            "ContinueAsync" => CountContinue(),
            _ => throw new NotSupportedException(method)
        });

        await reader.HandleRouteAsync(route, context,
            "/macbook-neo.html?product_id=125876&session=card-secret", new SemaphoreSlim(1, 1), default);
        Assert.Equal(1, aborts);
        Assert.Equal(0, continues);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("JobId=32", entry.Text);
        Assert.Contains("CardHref=https://www.cellphones.com.vn/macbook-neo.html", entry.Text);
        Assert.Contains("CardHrefPath=/macbook-neo.html", entry.Text);
        Assert.Contains("FinalRequestUri=https://evil.test/macbook-neo.html", entry.Text);
        Assert.Contains("FinalRequestPath=/macbook-neo.html", entry.Text);
        Assert.DoesNotContain("secret", entry.Text);

        requestUrl = "https://www.cellphones.com.vn/macbook-neo.html?product_id=125876";
        await reader.HandleRouteAsync(route, context, "/macbook-neo.html", new SemaphoreSlim(1, 1), default);
        Assert.Equal(1, aborts);
        Assert.Equal(1, continues);

        Task CountAbort() { aborts++; return Task.CompletedTask; }
        Task CountContinue() { continues++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Concurrent_failures_keep_their_own_context()
    {
        var stageValue = (int)Worker.CatalogReadStage.Navigation;
        var logger = new Capture<object>();
        async Task Fail(string source, int jobId)
        {
            var context = new Worker.CatalogReadContext(source, jobId);
            await Assert.ThrowsAsync<TimeoutException>(() => Worker.CatalogReadDiagnostics.ReadAsync<int>(logger, async () =>
            {
                context.Stage = (Worker.CatalogReadStage)stageValue;
                await Task.Yield();
                throw new TimeoutException("secret");
            }, context));
        }
        await Task.WhenAll(Fail("bhx", 27), Fail("cellphones", 28));
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains(logger.Entries, entry => entry.Text.Contains("Source=bhx; JobId=27; Stage=" + (Worker.CatalogReadStage)stageValue));
        Assert.Contains(logger.Entries, entry => entry.Text.Contains("Source=cellphones; JobId=28; Stage=" + (Worker.CatalogReadStage)stageValue));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("secret", entry.Text));
    }

    [Theory]
    [InlineData(true, "BrowserLaunch")]
    [InlineData(false, "BrowserLaunch")]
    [InlineData(true, "Navigation")]
    [InlineData(false, "Navigation")]
    [InlineData(true, "AccessCheck")]
    [InlineData(false, "AccessCheck")]
    [InlineData(true, "ProductCount")]
    [InlineData(false, "ProductCount")]
    [InlineData(true, "ProductFields")]
    [InlineData(false, "ProductFields")]
    public async Task Reader_reports_the_actual_failed_browser_stage(bool bhx, string stage)
    {
        var failure = new TimeoutException("untrusted token=private");
        var locator = Stub.Create<ILocator>((method, args) => method switch
        {
            "get_First" or "Nth" or "Locator" => null,
            "CountAsync" => Task.FromResult(1),
            "InnerTextAsync" when stage == "AccessCheck" => throw failure,
            "InnerTextAsync" => Task.FromResult("safe catalog body"),
            "GetAttributeAsync" => throw failure,
            _ => throw new NotSupportedException(method)
        });
        // Locator properties return the same bounded fixture; no browser/network is used.
        ((Stub)(object)locator).Self = locator;
        var response = Stub.Create<IResponse>((method, _) => method == "get_Status" ? 200 : throw new NotSupportedException(method));
        var page = Stub.Create<IPage>((method, args) => method switch
        {
            "SetDefaultNavigationTimeout" or "SetDefaultTimeout" => null,
            "RouteAsync" => Task.CompletedTask,
            "GotoAsync" when stage == "Navigation" => throw failure,
            "GotoAsync" => Task.FromResult<IResponse?>(response),
            "get_Url" => bhx ? "https://www.bachhoaxanh.com/bia" : "https://www.cellphones.com.vn/laptop/mac.html",
            "Locator" when stage == "ProductCount" && (string)args![0]! != "body" => throw failure,
            "Locator" => locator,
            _ => throw new NotSupportedException(method)
        });
        var browser = Stub.Create<IBrowser>((method, _) => method switch
        {
            "NewPageAsync" => Task.FromResult(page),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method)
        });
        var browserType = Stub.Create<IBrowserType>((method, _) => method == "LaunchAsync" ? Task.FromResult(browser) : throw new NotSupportedException(method));
        var playwright = Stub.Create<IPlaywright>((method, _) => method switch
        {
            "get_Chromium" => browserType,
            "Dispose" => null,
            _ => throw new NotSupportedException(method)
        });
        Task<IPlaywright> CreateBrowser() => stage == "BrowserLaunch" ? throw failure : Task.FromResult(playwright);
        using var client = new HttpClient(new RobotsHandler("User-agent: *\nAllow: /"));
        var logger = new Capture<Worker.BhxPlaywrightCatalogReader>();
        var cellLogger = new Capture<Worker.CellphoneSPlaywrightCatalogReader>();
        ISourceCatalogReader reader = bhx
            ? new Worker.BhxPlaywrightCatalogReader(() => client, logger, CreateBrowser)
            : new Worker.CellphoneSPlaywrightCatalogReader(_ => client, cellLogger, CreateBrowser);
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(
            bhx ? "bhx" : "cellphones", bhx ? "/bia" : "/laptop/mac.html", 29, default)));
        var entry = Assert.Single(bhx ? logger.Entries : cellLogger.Entries);
        Assert.Contains("Stage=" + stage, entry.Text);
        Assert.Contains("JobId=29", entry.Text);
        Assert.DoesNotContain("private", entry.Text);
        Assert.Null(entry.Exception);
    }

    public class Stub : DispatchProxy
    {
        private Func<string, object?[]?, object?> handler = null!;
        public object? Self { get; set; }
        public static T Create<T>(Func<string, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy).handler = handler;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Self is not null && method!.Name is "get_First" or "Nth" or "Locator") return Self;
            return handler(method!.Name, args);
        }
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
            Add(level, formatter(state, exception), exception);
        private void Add(LogLevel level, string text, Exception? exception)
        {
            lock (Entries) Entries.Add((level, text, exception));
        }
    }
}
