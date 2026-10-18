extern alias Workers;

using System.Net;
using WorkerIngestion = Workers::CompareShopAPI.Workers.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class BhxRequestGuardTests
{
    [Theory]
    [InlineData("https://bachhoaxanh.com/bia", true)]
    [InlineData("https://BACHHOAXANH.COM/robots.txt", true)]
    [InlineData("https://www.bachhoaxanh.com/bia", false)]
    [InlineData("http://bachhoaxanh.com/bia", false)]
    [InlineData("https://other.example/bia", false)]
    [InlineData("https://bachhoaxanh.com:444/bia", true)]
    [InlineData("https://user@bachhoaxanh.com/bia", true)]
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

        using var response = await client.GetAsync("https://bachhoaxanh.com/robots.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://bachhoaxanh.com/robots.txt", Assert.Single(transport.Requests).ToString());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("http://bachhoaxanh.com/robots.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("https://www.bachhoaxanh.com/robots.txt"));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Reader_uses_guarded_robots_request_before_playwright_navigation()
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("User-agent: *\nDisallow: /bia\n")
        });
        var reader = new WorkerIngestion.BhxPlaywrightCatalogReader(() => WorkerIngestion.BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(2), transport));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadValidatedBiaAsync(CancellationToken.None));
        Assert.Equal("https://bachhoaxanh.com/robots.txt", Assert.Single(transport.Requests).ToString());
    }

    [Theory]
    [InlineData("https://bachhoaxanh.com/robots.txt", true)]
    [InlineData("https://www.bachhoaxanh.com/robots.txt", false)]
    [InlineData("http://bachhoaxanh.com/robots.txt", false)]
    [InlineData("https://other.example/robots.txt", false)]
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://bachhoaxanh.com/robots.txt");

        if (allowed)
        {
            using var response = await WorkerIngestion.BhxRequestGuard.SendWithRedirectsAsync(client, request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, transport.Requests.Count);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkerIngestion.BhxRequestGuard.SendWithRedirectsAsync(client, request, CancellationToken.None));
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
}
