extern alias Workers;

using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Application.Ingestion;
using WorkerIngestion = Workers::CompareShopAPI.Workers.Ingestion;
using System.Net;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CellphoneSIngestionTests
{
    [Fact]
    public void Reader_and_category_are_scoped_to_stable_source_key()
    {
        var reader = new WorkerIngestion.CellphoneSPlaywrightCatalogReader();
        Assert.True(reader.Supports(DataSource.CellphoneSKey));
        Assert.False(reader.Supports(DataSource.BhxKey));
        Assert.True(DataSource.IsSupportedCategory(DataSource.CellphoneSKey, "/laptop/mac.html"));
        Assert.False(DataSource.IsSupportedCategory(DataSource.CellphoneSKey, "/laptop/"));
        Assert.False(DataSource.IsSupportedCategory(DataSource.BhxKey, "/laptop/mac.html"));
    }

    [Theory]
    [InlineData("https://www.cellphones.com.vn/laptop/mac.html", true)]
    [InlineData("https://cellphones.com.vn/product", true)]
    [InlineData("https://cdn.cellphones.com.vn/a.jpg", true)]
    [InlineData("http://www.cellphones.com.vn/laptop/mac.html", false)]
    [InlineData("https://evil.cellphones.com.vn/laptop/mac.html", false)]
    [InlineData("https://www.cellphones.com.vn:444/laptop/mac.html", false)]
    [InlineData("https://www.cellphones.com.vn@evil.test/laptop/mac.html", false)]
    public void Request_policy_allows_only_exact_https_hosts_and_default_port(string value, bool expected)
    {
        Assert.Equal(expected, WorkerIngestion.CellphoneSRequestGuard.IsAllowed(new Uri(value)));
    }

    [Fact]
    public async Task Product_path_is_limited_and_redirect_targets_are_revalidated()
    {
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/admin")));
        Assert.True(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cdn2.cellphones.com.vn/images/mac.jpg")));
        using var client = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/redirect") } }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.cellphones.com.vn/robots.txt");
        await Assert.ThrowsAsync<SourceRedirectDeniedException>(() => WorkerIngestion.CellphoneSRequestGuard.SendWithRedirectsAsync(client, request, default));
    }

    [Fact]
    public async Task Existing_Bhx_reader_and_proxy_keep_their_validated_routes()
    {
        var bhx = new WorkerIngestion.BhxPlaywrightCatalogReader();
        await Assert.ThrowsAsync<InvalidDataException>(() => bhx.ReadAsync(DataSource.CellphoneSKey, "/bia", default));
        await using var proxy = await WorkerIngestion.BhxForwardProxy.StartAsync();
        Assert.StartsWith("http://127.0.0.1:", proxy.Server);
    }

    [Fact]
    public async Task Source_reader_default_fails_when_legacy_method_is_not_implemented()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => ((ISourceCatalogReader)new EmptyReader()).ReadValidatedBiaAsync(default));
    }

    [Fact]
    public async Task Blocked_status_fails_closed()
    {
        using var client = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.cellphones.com.vn/robots.txt");
        await Assert.ThrowsAsync<SourceAccessBlockedException>(() => WorkerIngestion.CellphoneSRequestGuard.SendWithRedirectsAsync(client, request, default));
    }

    [Fact]
    public async Task Reader_rejects_unapproved_category_and_robots_denial()
    {
        var invalid = new WorkerIngestion.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))));
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid.ReadAsync(DataSource.CellphoneSKey, "/laptop/", default));

        var blocked = new WorkerIngestion.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsAsync<SourceAccessBlockedException>(() => blocked.ReadAsync(DataSource.CellphoneSKey, "/laptop/mac.html", default));

        var robotsDenied = new WorkerIngestion.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nDisallow: /laptop/") })));
        await Assert.ThrowsAsync<InvalidDataException>(() => robotsDenied.ReadAsync(DataSource.CellphoneSKey, "/laptop/mac.html", default));
    }

    [Fact]
    public void Parser_and_robots_policy_handle_normal_values_and_denials()
    {
        Assert.Equal(24990000, WorkerIngestion.CellphoneSPlaywrightCatalogReader.ParsePrice("24.990.000₫"));
        Assert.Equal(0, WorkerIngestion.CellphoneSPlaywrightCatalogReader.ParsePrice("Liên hệ"));
        Assert.True(WorkerIngestion.CellphoneSPlaywrightCatalogReader.IsDisallowed("User-agent: *\nDisallow: /laptop/", "/laptop/mac.html"));
        Assert.False(WorkerIngestion.CellphoneSPlaywrightCatalogReader.IsDisallowed("User-agent: *\nDisallow: /admin", "/laptop/mac.html"));
        Assert.NotEmpty(WorkerIngestion.CellphoneSPlaywrightCatalogReader.ParseSpecifications("Apple M5 10-core GPU 16GB 512GB"));
    }

    [Fact]
    public void Scraped_offer_rejects_bad_price_url_and_stock()
    {
        Assert.True(new ScrapedOffer("sku", "MacBook", "Apple", 100, "https://cellphones.com.vn/p", StockStatus: "in_stock").IsValid);
        Assert.False(new ScrapedOffer("sku", "MacBook", "Apple", 100, "http://cellphones.com.vn/p").IsValid);
        Assert.False(new ScrapedOffer("sku", "MacBook", "Apple", 100, "https://cellphones.com.vn/p", OriginalPriceVnd: 99).IsValid);
        Assert.False(new ScrapedOffer("sku", "MacBook", "Apple", 100, "https://cellphones.com.vn/p", StockStatus: "maybe").IsValid);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class EmptyReader : CompareShopAPI.Application.Ingestion.ISourceCatalogReader { }
}
