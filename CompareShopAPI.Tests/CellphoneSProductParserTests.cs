extern alias Workers;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Ingestion;
using Microsoft.Playwright;
using Worker = Workers::CompareShopAPI.Workers.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CellphoneSProductParserTests
{
    private static readonly Fixture SourceFixture = JsonSerializer.Deserialize<Fixture>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cellphones-macbook-neo-2026-10-29.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private static readonly Worker.CellphoneSListingProvenance ApprovedProvenance = CreateProvenance(SourceFixture.ProductUrl);

    private static Worker.CellphoneSListingProvenance CreateProvenance(params string[] hrefs)
    {
        var html = string.Join(string.Empty, hrefs.Select(href => $"<div class='product-info'><a href='{href}'></a></div>"));
        Assert.True(Worker.CellphoneSListingProvenance.TryCreate(new Uri("https://cellphones.com.vn/laptop/mac.html"), html, out var provenance));
        return provenance!;
    }

    [Fact]
    public void Captured_source_fixture_resolves_the_matching_published_variant_id()
    {
        Assert.True(SourceFixture.RobotsAllowedCategory);
        Assert.True(SourceFixture.RobotsAllowedProductPath);
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();

        var sourceId = Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl),
            Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(SourceFixture.CategoryProduct.CurrentPriceText), variants);

        Assert.Equal("125876", sourceId);
        Assert.NotEqual(SourceFixture.PublishedProductJsonLdSku, sourceId);
    }

    [Fact]
    public void Product_identity_requires_listing_provenance()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(null, new Uri(SourceFixture.ProductUrl),
            18_790_000, variants));
    }

    [Fact]
    public async Task Reader_uses_published_category_prices_and_detail_variant_identity()
    {
        var productPage = CreateLocator((method, args) => method switch
        {
            "Locator" => (string)args![0]! switch
            {
                ".product__price--show" => TextLocator("18.790.000đ"),
                ".product__name" => TextLocator(SourceFixture.CategoryProduct.Name),
                "a[href]" => AttributeLocator("href", SourceFixture.ProductUrl),
                "img" => AttributeLocator("src", "https://cdn2.cellphones.com.vn/images/macbook.png"),
                ".product__price--through" => TextLocator("19.490.000đ"),
                _ => throw new NotSupportedException((string)args[0]!)
            },
            "InnerTextAsync" => Task.FromResult(SourceFixture.CategoryProduct.Name + " 18.790.000đ"),
            _ => throw new NotSupportedException(method)
        });
        var activeVariant = CreateLocator((method, args) => method switch
        {
            "getAttribute" or "GetAttributeAsync" => Task.FromResult((string)args![0]! switch
            {
                "data-product-id" => "125876",
                "class" => "item-variant active",
                _ => null
            }),
            "Locator" => (string)args![0]! switch
            {
                "a[href]" => AttributeLocator("href", SourceFixture.DetailVariants.Single(x => x.IsActive).Href),
                ".item-variant-price" => TextLocator("18.790.000₫"),
                _ => throw new NotSupportedException((string)args[0]!)
            },
            _ => throw new NotSupportedException(method)
        });
        var cards = CollectionLocator(productPage);
        var variants = CollectionLocator(activeVariant);
        var gotoCount = 0;
        var response = CreateProxy<IResponse>((method, _) => method switch
        {
            "get_Status" => 200,
            "get_Url" => "https://cellphones.com.vn/laptop/mac.html",
            "BodyAsync" => Task.FromResult(System.Text.Encoding.UTF8.GetBytes($"<div class='product-info'><a href='{SourceFixture.ProductUrl}'></a></div>")),
            _ => throw new NotSupportedException(method)
        });
        var page = CreateProxy<IPage>((method, args) => method switch
        {
            "SetDefaultNavigationTimeout" or "SetDefaultTimeout" => null,
            "RouteAsync" => Task.CompletedTask,
            "GotoAsync" => GotoResponse(response, ref gotoCount),
            "get_Url" => gotoCount < 2 ? "https://cellphones.com.vn/laptop/mac.html" : SourceFixture.ProductUrl,
            "Locator" => (string)args![0]! switch
            {
                "body" => TextLocator("Mac catalog"),
                ".product-info" => cards,
                "li.item-variant[data-product-id]" => variants,
                _ => throw new NotSupportedException((string)args[0]!)
            },
            _ => throw new NotSupportedException(method)
        });
        var browser = CreateProxy<IBrowser>((method, _) => method switch
        {
            "NewPageAsync" => Task.FromResult(page),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method)
        });
        var browserType = CreateProxy<IBrowserType>((method, _) => method == "LaunchAsync"
            ? Task.FromResult(browser) : throw new NotSupportedException(method));
        var playwright = CreateProxy<IPlaywright>((method, _) => method switch
        {
            "get_Chromium" => browserType,
            "Dispose" => null,
            _ => throw new NotSupportedException(method)
        });
        using var robotsClient = new HttpClient(new RobotsHandler());
        var reader = new Worker.CellphoneSPlaywrightCatalogReader(_ => robotsClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker.CellphoneSPlaywrightCatalogReader>.Instance,
            () => Task.FromResult(playwright));

        var offer = Assert.Single(await reader.ReadAsync(DataSource.CellphoneSKey, "/laptop/mac.html", 57, default));

        Assert.Equal("125876", offer.ExternalSku);
        Assert.Equal(18_790_000, offer.PriceVnd);
        Assert.Equal(19_490_000, offer.OriginalPriceVnd);
        Assert.Equal(2, gotoCount);
    }

    [Fact]
    public void Missing_or_conflicting_source_ids_fail_closed()
    {
        var expectedUrl = new Uri(SourceFixture.ProductUrl);
        var active = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_790_000,
            active.Select(row => row with { Id = row.IsActive ? null : row.Id }).ToArray()));
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_790_000,
            active.Select(row => row with { Href = row.IsActive ? row.Href!.Replace("125876", "125877", StringComparison.Ordinal) : row.Href }).ToArray()));
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_790_000,
            active.Select(row => row with { IsActive = true }).ToArray()));
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_990_000, active));
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_790_000,
            active.Select(row => row with { Id = "125876" }).ToArray()));
    }

    [Fact]
    public void Listing_query_id_must_match_active_variant_even_when_price_matches()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        var listingUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125875");

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, listingUrl, 18_790_000, variants));
    }

    [Fact]
    public void Final_detail_query_id_must_match_active_variant()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        var finalUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125875");

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl),
            18_790_000, variants, finalUrl));
    }

    [Fact]
    public void Redirect_changing_listing_product_id_fails_closed_even_when_final_id_matches_active()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        var listingUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125875");
        var finalUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125876");

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, listingUrl,
            18_790_000, variants, finalUrl));
    }

    [Theory]
    [InlineData("?product_id=125876&product_id=125876")]
    [InlineData("?product_id=125876&product_id=125875")]
    public void Duplicate_listing_product_ids_are_rejected(string query)
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl + query),
            18_790_000, variants));
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl),
            18_790_000, variants, new Uri(SourceFixture.ProductUrl + query)));
    }

    [Fact]
    public void Matching_explicit_listing_and_final_ids_resolve_active_variant()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        var expectedUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125876");
        var finalUrl = new Uri(SourceFixture.ProductUrl + "?product_id=125876");

        Assert.Equal("125876", Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, expectedUrl, 18_790_000, variants, finalUrl));
    }

    [Fact]
    public void Duplicate_variant_product_ids_are_rejected()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        variants[3] = variants[3] with { Href = variants[3].Href + "&product_id=125876" };

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl),
            18_790_000, variants));
    }

    [Fact]
    public void Conflicting_variant_product_ids_are_rejected()
    {
        var variants = SourceFixture.DetailVariants.Select(variant => new Worker.CellphoneSProductVariant(
            variant.Id, variant.Href, Worker.CellphoneSPlaywrightCatalogReader.ParsePrice(variant.PriceText), variant.IsActive)).ToArray();
        variants[3] = variants[3] with { Href = variants[3].Href!.Replace("125876", "125875", StringComparison.Ordinal) + "&product_id=125876" };

        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl),
            18_790_000, variants));
    }

    [Theory]
    [InlineData("https://evil.example/macbook-neo-13-a18-pro-6-cpu-5-gpu-8gb-256gb.html?product_id=125876")]
    [InlineData("/macbook-air-m4-13-inch.html?product_id=125876")]
    public void Variant_identity_cannot_escape_the_approved_product_url(string href)
    {
        var variants = new[] { new Worker.CellphoneSProductVariant("125876", href, 18_790_000, true) };
        Assert.Throws<InvalidDataException>(() => Worker.CellphoneSProductIdentity.Resolve(ApprovedProvenance, new Uri(SourceFixture.ProductUrl), 18_790_000, variants));
        Assert.False(Worker.CellphoneSRequestGuard.IsAllowedProductPage(new Uri("https://bhxcdn.vn/macbook.jpg")));
    }

    private static object GotoResponse(IResponse response, ref int count)
    {
        count++;
        return Task.FromResult<IResponse?>(response);
    }

    private static ILocator TextLocator(string text) => CreateLocator((method, _) => method switch
    {
        "get_First" => null,
        "InnerTextAsync" => Task.FromResult(text),
        _ => throw new NotSupportedException(method)
    }, self: true);

    private static ILocator AttributeLocator(string attribute, string value) => CreateLocator((method, args) => method switch
    {
        "get_First" => null,
        "GetAttributeAsync" => Task.FromResult((string)args![0]! == attribute ? value : null),
        _ => throw new NotSupportedException(method)
    }, self: true);

    private static ILocator CollectionLocator(ILocator item) => CreateLocator((method, args) => method switch
    {
        "CountAsync" => Task.FromResult(1),
        "Nth" => item,
        "get_First" => item,
        _ => throw new NotSupportedException(method)
    });

    private static ILocator CreateLocator(Func<string, object?[]?, object?> handler, bool self = false)
    {
        ILocator? proxy = null;
        proxy = CreateProxy<ILocator>((method, args) => method switch
        {
            "get_First" when self => proxy,
            _ => handler(method, args)
        });
        return proxy;
    }

    private static T CreateProxy<T>(Func<string, object?[]?, object?> handler) where T : class =>
        Stub.Create<T>((method, args) => handler(method.Name, args));

    private class Stub : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;

        internal static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var value = Create<T, Stub>();
            ((Stub)(object)value).handler = handler;
            return value;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!, args);
    }

    private sealed class RobotsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nAllow: /\n") });
    }

    private sealed record Fixture(
        string ProductUrl,
        string CategoryUrl,
        bool RobotsAllowedCategory,
        bool RobotsAllowedProductPath,
        string PublishedProductJsonLdSku,
        CategoryFixture CategoryProduct,
        VariantFixture[] DetailVariants);

    private sealed record CategoryFixture(string Name, string CurrentPriceText, string OriginalPriceText, string ProductHref);
    private sealed record VariantFixture(string Id, bool IsActive, string Href, string Title, string PriceText);
}
