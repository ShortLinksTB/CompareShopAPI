extern alias Workers;

using System.Net;
using Microsoft.Extensions.Logging;
using WorkerIngestion = Workers::CompareShopAPI.Workers.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class BhxRequestGuardTests
{
    [Theory]
    [InlineData("https://www.bachhoaxanh.com/bia", true)]
    [InlineData("https://WWW.BACHHOAXANH.COM/robots.txt", true)]
    [InlineData("https://bachhoaxanh.com/bia", false)]
    [InlineData("http://www.bachhoaxanh.com/bia", false)]
    [InlineData("https://other.example/bia", false)]
    [InlineData("https://www.bachhoaxanh.com:444/bia", false)]
    [InlineData("https://user@www.bachhoaxanh.com/bia", false)]
    [InlineData("not a url", false)]
    public void Playwright_guard_allows_only_exact_https_host(string url, bool expected)
    {
        Assert.Equal(expected, WorkerIngestion.BhxRequestGuard.IsAllowedPlaywrightRequest(url));
    }

    [Fact]
    public async Task Http_client_guard_allows_robots_request_and_rejects_unapproved_targets()
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport);

        using var response = await client.GetAsync("https://www.bachhoaxanh.com/robots.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://www.bachhoaxanh.com/robots.txt", Assert.Single(transport.Requests).ToString());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("https://bachhoaxanh.com/robots.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("http://www.bachhoaxanh.com/robots.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("https://www.bachhoaxanh.com:444/robots.txt"));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Reader_uses_canonical_www_host_for_guarded_robots_request()
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("User-agent: *\nDisallow: /bia\n")
        });
        var reader = new WorkerIngestion.BhxPlaywrightCatalogReader(() => WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadValidatedBiaAsync(CancellationToken.None));
        Assert.Equal("https://www.bachhoaxanh.com/robots.txt", Assert.Single(transport.Requests).ToString());
    }

    [Fact]
    public async Task Reader_stops_when_robots_request_is_blocked()
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var reader = new WorkerIngestion.BhxPlaywrightCatalogReader(() => WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport));

        await Assert.ThrowsAsync<CompareShopAPI.Application.Ingestion.SourceAccessBlockedException>(() => reader.ReadValidatedBiaAsync(CancellationToken.None));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Reader_logs_exception_type_and_shortened_stack_without_exception_message()
    {
        const string sensitiveResponseDetail = "raw response body with cookie=private-value";
        var transport = new ThrowingHandler(new InvalidOperationException(sensitiveResponseDetail));
        var logger = new CapturingLogger<WorkerIngestion.BhxPlaywrightCatalogReader>();
        var reader = new WorkerIngestion.BhxPlaywrightCatalogReader(
            () => WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport), logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadValidatedBiaAsync(CancellationToken.None));

        var (level, diagnostic) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains("SendAsync", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveResponseDetail, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", diagnostic, StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= 1400);
    }

    [Theory]
    [InlineData(403, "https://www.bachhoaxanh.com/bia", "", true)]
    [InlineData(null, "https://www.bachhoaxanh.com/captcha", "", true)]
    [InlineData(null, "https://www.bachhoaxanh.com/challenge", "", true)]
    [InlineData(null, "https://www.bachhoaxanh.com/bia", "captcha", true)]
    [InlineData(null, "https://www.bachhoaxanh.com/bia", "verify you are human", true)]
    [InlineData(null, "https://www.bachhoaxanh.com/bia", "truy cập bất thường", true)]
    [InlineData(200, "https://www.bachhoaxanh.com/bia", "valid catalog", false)]
    public void Reader_rejects_blocked_or_challenge_responses(int? status, string url, string pageText, bool blocked)
    {
        if (blocked)
            Assert.Throws<CompareShopAPI.Application.Ingestion.SourceAccessBlockedException>(() => WorkerIngestion.BhxPlaywrightCatalogReader.EnsureAccessible(status, url, pageText));
        else
            WorkerIngestion.BhxPlaywrightCatalogReader.EnsureAccessible(status, url, pageText);
    }

    [Theory]
    [InlineData("https://www.bachhoaxanh.com/robots.txt", true)]
    [InlineData("https://bachhoaxanh.com/robots.txt", false)]
    [InlineData("http://www.bachhoaxanh.com/robots.txt", false)]
    [InlineData("https://other.example/robots.txt", false)]
    [InlineData("https://www.bachhoaxanh.com:444/robots.txt", false)]
    public async Task Guarded_http_send_checks_each_redirect_target(string redirectTarget, bool allowed)
    {
        var requestCount = 0;
        var transport = new RecordingHandler(_ =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(redirectTarget);
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bachhoaxanh.com/robots.txt");

        if (allowed)
        {
            using var response = await WorkerIngestion.BhxRequestGuard.SendWithRedirectsAsync(client, request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, transport.Requests.Count);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<CompareShopAPI.Application.Ingestion.SourceRedirectDeniedException>(
                () => WorkerIngestion.BhxRequestGuard.SendWithRedirectsAsync(client, request, CancellationToken.None));
            Assert.Equal("redirect_target_denied_302", exception.ErrorCode);
            Assert.Equal(CompareShopAPI.Application.Ingestion.SourceRedirectDeniedException.SanitizedSummary, exception.Message);
            Assert.DoesNotContain(redirectTarget, exception.Message, StringComparison.Ordinal);
            Assert.Single(transport.Requests);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw failure;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
