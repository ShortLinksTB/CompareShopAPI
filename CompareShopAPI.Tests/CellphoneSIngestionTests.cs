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
    [InlineData("cellphones", "/laptop/mac.html")]
    [InlineData("cellphones-asus", "/laptop/asus.html")]
    [InlineData("cellphones-lenovo", "/laptop/lenovo.html")]
    [InlineData("cellphones-dell", "/laptop/dell.html")]
    [InlineData("cellphones-hp", "/laptop/hp.html")]
    [InlineData("cellphones-acer", "/laptop/acer.html")]
    public void Only_the_six_approved_source_path_pairs_are_supported(string key, string path)
    {
        Assert.True(new WorkerIngestion.CellphoneSPlaywrightCatalogReader().Supports(key));
        Assert.True(DataSource.IsSupportedCategory(key, path));
        Assert.Throws<ArgumentException>(() => new DataSource(key, key, path == "/laptop/mac.html" ? "/laptop/asus.html" : "/laptop/mac.html", null, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("cellphones", "/laptop/asus.html")]
    [InlineData("cellphones-asus", "/laptop/mac.html")]
    [InlineData("cellphones-unknown", "/laptop/unknown.html")]
    [InlineData("cellphones-dell", "/laptop/dell.html/extra")]
    public void Every_other_source_path_pair_is_rejected(string key, string path)
    {
        Assert.False(DataSource.IsSupportedCategory(key, path));
        Assert.Throws<ArgumentException>(() => new DataSource(key, key, path, null, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("https://www.cellphones.com.vn/laptop/mac.html", true)]
    [InlineData("https://cellphones.com.vn/macbook-air-m4-13-inch.html", true)]
    [InlineData("http://www.cellphones.com.vn/laptop/mac.html", false)]
    [InlineData("https://evil.cellphones.com.vn/laptop/mac.html", false)]
    [InlineData("https://www.cellphones.com.vn:444/laptop/mac.html", false)]
    [InlineData("https://www.cellphones.com.vn@evil.test/laptop/mac.html", false)]
    public void Request_policy_allows_only_exact_https_hosts_and_default_port(string value, bool expected)
    {
        Assert.Equal(expected, WorkerIngestion.CellphoneSRequestGuard.IsAllowed(new Uri(value)));
    }

    [Fact]
    public void Diagnostic_url_retains_path_and_redacts_credentials_all_query_values_and_fragment()
    {
        var safe = WorkerIngestion.CellphoneSRequestGuard.SafeUrlForDiagnostics(
            "https://user:password@cellphones.com.vn/macbook-neo.html?product_id=125876&token=session-secret&utm_source=private#auth-secret");

        Assert.Equal("https://cellphones.com.vn/macbook-neo.html", safe);
        Assert.Equal("/macbook-neo.html", WorkerIngestion.CellphoneSRequestGuard.SafePathForDiagnostics(
            "https://user:password@cellphones.com.vn/macbook-neo.html?product_id=125876&token=session-secret&utm_source=private#auth-secret"));
        Assert.DoesNotContain("password", safe);
        Assert.DoesNotContain("session-secret", safe);
        Assert.DoesNotContain("private", safe);
        Assert.DoesNotContain("auth-secret", safe);
        Assert.Equal("[invalid-uri]", WorkerIngestion.CellphoneSRequestGuard.SafePathForDiagnostics("%%%"));
    }

    [Fact]
    public void Diagnostic_url_resolves_real_relative_card_href_and_drops_duplicate_product_ids()
    {
        var category = new Uri("https://www.cellphones.com.vn/laptop/mac.html");
        var safe = WorkerIngestion.CellphoneSRequestGuard.SafeUrlForDiagnostics(
            "/macbook-neo-13-a18-pro.html?product_id=125876&product_id=125877&sid=secret", category);

        Assert.Equal("https://www.cellphones.com.vn/macbook-neo-13-a18-pro.html", safe);
        Assert.DoesNotContain("secret", safe);
        Assert.Equal("[invalid-uri]", WorkerIngestion.CellphoneSRequestGuard.SafeUrlForDiagnostics("%%%"));
    }

    [Fact]
    public async Task Product_path_is_limited_and_redirect_targets_are_revalidated()
    {
        Assert.True(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/laptop/mac.html")));
        foreach (var path in new[] { "asus", "lenovo", "dell", "hp", "acer" })
            Assert.True(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri($"https://cellphones.com.vn/laptop/{path}.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/laptop/windows.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("http://cellphones.com.vn/laptop/asus.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn:444/laptop/asus.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/laptop/mac.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/macbook-air-m4-13-inch.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html/extra")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.php")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cellphones.com.vn/iphone-17-pro.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/laptop/windows.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/laptop/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html/offers")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/iphone-17.html")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/admin")));
        Assert.True(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cdn.cellphones.com.vn/images/macbook.jpg")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cdn.cellphones.com.vn/admin")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cdn.cellphones.com.vn/images/macbook.js")));
        Assert.False(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://www.cellphones.com.vn/admin")));
        Assert.True(WorkerIngestion.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://cdn2.cellphones.com.vn/images/mac.jpg")));
        var provenance = CreateProvenance("https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html");
        using var client = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/redirect") } }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html");
        await Assert.ThrowsAsync<SourceRedirectDeniedException>(() => WorkerIngestion.CellphoneSRequestGuard.SendWithRedirectsAsync(client, request, provenance.Contains, default));
    }

    [Fact]
    public async Task Mac_mini_href_requires_product_card_provenance_from_exact_listing_response()
    {
        const string m6 = "https://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html";
        const string m4 = "https://cellphones.com.vn/apple-macmini-m4-10cpu-10gpu-24gb-256gb-2024-z1cf.html";
        var provenance = CreateProvenance(m6, m4);

        Assert.True(provenance.Contains(new Uri(m6 + "?utm_source=card")));
        Assert.True(provenance.Contains(new Uri(m4)));
        Assert.False(provenance.Contains(new Uri("https://cellphones.com.vn/apple-macmini-m4-guessed.html")));
        Assert.False(provenance.Contains(new Uri("https://www.cellphones.com.vn/" + m6.Split('/').Last())));

        var scriptAndMultipleLinks = "<script>" +
            "<div class='product-info'><a href='https://cellphones.com.vn/script-guessed.html'></a></div>" +
            "</script><div class='product-info'><a href='" + m6 + "'></a><a href='https://cellphones.com.vn/card-guessed.html'></a></div>";
        Assert.True(WorkerIngestion.CellphoneSListingProvenance.TryCreate(new Uri("https://cellphones.com.vn/laptop/mac.html"), scriptAndMultipleLinks, out var parsed));
        Assert.True(parsed!.Contains(new Uri(m6)));
        Assert.False(parsed.Contains(new Uri("https://cellphones.com.vn/script-guessed.html")));
        Assert.False(parsed.Contains(new Uri("https://cellphones.com.vn/card-guessed.html")));

        var nonCard = "<div class='navigation'><a href='" + m6 + "'></a></div>";
        Assert.False(WorkerIngestion.CellphoneSListingProvenance.TryCreate(new Uri("https://cellphones.com.vn/laptop/mac.html"), nonCard, out _));
        Assert.False(WorkerIngestion.CellphoneSListingProvenance.TryCreate(new Uri("https://cellphones.com.vn/laptop/windows.html"),
            "<div class='product-info'><a href='" + m6 + "'></a></div>", out _));
        Assert.False(WorkerIngestion.CellphoneSListingProvenance.TryCreate(new Uri("http://cellphones.com.vn/laptop/mac.html"),
            "<div class='product-info'><a href='" + m6 + "'></a></div>", out _));

        using var downgradeClient = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("http://cellphones.com.vn/mac-mini-m6-12cpu-12gpu-16gb-256gb-2026.html") } }));
        using var request = new HttpRequestMessage(HttpMethod.Get, m6);
        await Assert.ThrowsAsync<SourceRedirectDeniedException>(() => WorkerIngestion.CellphoneSRequestGuard.SendWithRedirectsAsync(downgradeClient, request, provenance.Contains, default));
    }

    [Fact]
    public async Task Reader_rejects_mac_mini_url_outside_the_approved_category()
    {
        var reader = new WorkerIngestion.CellphoneSPlaywrightCatalogReader(_ => new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(DataSource.CellphoneSKey, "/laptop/", default));
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

    [Theory]
    [InlineData("cellphones", "Apple M5 16GB 512GB 13.6 inch", "Apple", "chip")]
    [InlineData("cellphones-asus", "16GB 512GB 15.6 inches", "Asus", "processor")]
    [InlineData("cellphones-lenovo", "32GB 1TB 14 inches", "Lenovo", "processor")]
    [InlineData("cellphones-dell", "16GB 512GB 15.6 inches", "Dell", "processor")]
    [InlineData("cellphones-hp", "16GB 512GB 14 inches", "HP", "processor")]
    [InlineData("cellphones-acer", "8GB 256GB 14 inches", "Acer", "processor")]
    public void Category_metadata_is_source_specific_and_required_specs_fail_closed(string key, string text, string brand, string processorKey)
    {
        var specs = WorkerIngestion.CellphoneSPlaywrightCatalogReader.ParseSpecifications(text, key);
        Assert.Equal(brand, DataSource.CellphoneSBrands[key]);
        if (key == DataSource.CellphoneSKey) Assert.True(specs.ContainsKey(processorKey));
        else Assert.False(specs.ContainsKey(processorKey));
        Assert.True(WorkerIngestion.CellphoneSPlaywrightCatalogReader.HasRequiredSpecifications(specs, key));
        var productName = key switch
        {
            "cellphones" => "Apple MacBook",
            "cellphones-asus" => "Asus Vivobook",
            "cellphones-lenovo" => "Lenovo ThinkPad",
            "cellphones-dell" => "Dell XPS",
            "cellphones-hp" => "HP Pavilion",
            _ => "Acer Aspire"
        };
        Assert.True(WorkerIngestion.CellphoneSPlaywrightCatalogReader.IsCategoryProductValid(productName, specs, key));
        Assert.False(WorkerIngestion.CellphoneSPlaywrightCatalogReader.IsCategoryProductValid("Apple MacBook", specs, key == "cellphones" ? "cellphones-asus" : key));
        Assert.Throws<InvalidDataException>(() => WorkerIngestion.CellphoneSPlaywrightCatalogReader.ValidateCategoryProduct("wrong brand", specs, key));
        Assert.False(WorkerIngestion.CellphoneSPlaywrightCatalogReader.HasRequiredSpecifications(
            specs.Where(pair => pair.Key != "storage").ToDictionary(pair => pair.Key, pair => pair.Value), key));
        if (key == DataSource.CellphoneSKey)
            Assert.False(WorkerIngestion.CellphoneSPlaywrightCatalogReader.HasRequiredSpecifications(
                specs.Where(pair => pair.Key != "chip").ToDictionary(pair => pair.Key, pair => pair.Value), key));
        Assert.False(WorkerIngestion.CellphoneSPlaywrightCatalogReader.HasRequiredSpecifications(specs, "unknown-cellphones-category"));
        if (key != DataSource.CellphoneSKey) Assert.DoesNotContain("Apple", string.Join(" ", specs.Values));
    }

    [Theory]
    [InlineData("14 inch", "14 inch")]
    [InlineData("14 inches", "14 inches")]
    public void Screen_parser_accepts_singular_and_plural_inch_units(string text, string expected)
    {
        var specs = WorkerIngestion.CellphoneSPlaywrightCatalogReader.ParseSpecifications($"Apple M5 16GB 512GB {text}");

        Assert.Equal(expected, specs["screen"]);
        Assert.True(WorkerIngestion.CellphoneSPlaywrightCatalogReader.HasRequiredSpecifications(specs, DataSource.CellphoneSKey));
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

    private static WorkerIngestion.CellphoneSListingProvenance CreateProvenance(params string[] hrefs)
    {
        var html = string.Join(string.Empty, hrefs.Select(href => "<div class='product-info'><a href='" + href + "'></a></div>"));
        Assert.True(WorkerIngestion.CellphoneSListingProvenance.TryCreate(new Uri("https://cellphones.com.vn/laptop/mac.html"), html, out var provenance));
        return provenance!;
    }

    private sealed class EmptyReader : CompareShopAPI.Application.Ingestion.ISourceCatalogReader { }
}
