using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Ingestion;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class CellphoneSPlaywrightCatalogReader : ISourceCatalogReader
{
    private static readonly Uri CategoryUri = CellphoneSListingProvenance.ApprovedListing;
    private const string UserAgent = "CompareShopPriceMonitor/1.0 (+https://giamha.online)";
    private const float ProductFieldActionTimeoutMs = 8_000;
    private readonly Func<string, HttpClient> robotsClientFactory;

    private readonly Func<Task<IPlaywright>> playwrightFactory;
    private readonly ILogger<CellphoneSPlaywrightCatalogReader> logger;

    public CellphoneSPlaywrightCatalogReader() : this(CreateRobotsClient, NullLogger<CellphoneSPlaywrightCatalogReader>.Instance) { }
    public CellphoneSPlaywrightCatalogReader(ILogger<CellphoneSPlaywrightCatalogReader> logger) : this(CreateRobotsClient, logger) { }
    internal CellphoneSPlaywrightCatalogReader(Func<string, HttpClient> robotsClientFactory)
        : this(robotsClientFactory, NullLogger<CellphoneSPlaywrightCatalogReader>.Instance) { }
    internal CellphoneSPlaywrightCatalogReader(Func<string, HttpClient> robotsClientFactory, ILogger<CellphoneSPlaywrightCatalogReader> logger, Func<Task<IPlaywright>>? playwrightFactory = null)
    {
        this.robotsClientFactory = robotsClientFactory;
        this.logger = logger;
        this.playwrightFactory = playwrightFactory ?? Playwright.CreateAsync;
    }

    public bool Supports(string sourceKey) => sourceKey == DataSource.CellphoneSKey;

    public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, CancellationToken ct)
        => ReadWithContextAsync(sourceKey, categoryPath, null, ct);

    public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct)
        => ReadWithContextAsync(sourceKey, categoryPath, jobId, ct);

    private Task<IReadOnlyList<ScrapedOffer>> ReadWithContextAsync(string sourceKey, string categoryPath, int? jobId, CancellationToken ct)
    {
        if (!Supports(sourceKey) || categoryPath != "/laptop/mac.html") throw new InvalidDataException("CellphoneS category is not supported.");
        var context = new CatalogReadContext(DataSource.CellphoneSKey, jobId);
        return CatalogReadDiagnostics.ReadAsync(logger, () => ReadValidatedCoreAsync(context, ct), context);
    }

    private async Task<IReadOnlyList<ScrapedOffer>> ReadValidatedCoreAsync(CatalogReadContext context, CancellationToken ct)
    {
        context.Stage = CatalogReadStage.Robots;
        await using var proxy = await BhxForwardProxy.StartAsync(CellphoneSRequestGuard.AllowedHosts, ct);
        using var robotsClient = robotsClientFactory(proxy.Server);
        using var robotsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(CategoryUri, "/robots.txt"));
        robotsRequest.Headers.UserAgent.ParseAdd(UserAgent);
        using var robots = await CellphoneSRequestGuard.SendWithRedirectsAsync(robotsClient, robotsRequest, ct);
        if (!robots.IsSuccessStatusCode) throw new SourceAccessBlockedException();
        var robotsText = await robots.Content.ReadAsStringAsync(ct);
        if (IsDisallowed(robotsText, "/laptop/mac.html")) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.RobotsPolicyDenied);

        context.Stage = CatalogReadStage.BrowserLaunch;
        using var playwright = await playwrightFactory();
        await using var browser = await playwright.Chromium.LaunchAsync(BhxBrowserLaunchOptions.Create(proxy.Server));
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { AcceptDownloads = false, UserAgent = UserAgent });
        page.SetDefaultNavigationTimeout(20_000); page.SetDefaultTimeout(ProductFieldActionTimeoutMs);
        using var gate = new SemaphoreSlim(1, 1);
        string? activeCardHref = null;
        CellphoneSListingProvenance? provenance = null;
        await page.RouteAsync("**/*", route => HandleRouteAsync(route, context, activeCardHref, () => provenance, gate, ct));
        context.Stage = CatalogReadStage.Navigation;
        var response = await page.GotoAsync(CategoryUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
        context.Stage = CatalogReadStage.AccessCheck;
        if (response is null || !CellphoneSListingProvenance.TryCreate(new Uri(response.Url), System.Text.Encoding.UTF8.GetString(await response.BodyAsync()), out var extractedProvenance))
            throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);
        var approvedListing = extractedProvenance!;
        provenance = approvedListing;
        BhxPlaywrightCatalogReader.EnsureAccessible(response?.Status, page.Url, (await page.Locator("body").InnerTextAsync()).ToLowerInvariant());
        context.Stage = CatalogReadStage.ProductCount;
        var cards = page.Locator(".product-info");
        var count = await cards.CountAsync();
        if (count is < 1 or > 300) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductCountInvalid);
        var categoryProducts = new List<CellphoneSCategoryProduct>(count);
        context.Stage = CatalogReadStage.ProductFields;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var card = cards.Nth(i);
            var anchor = card.Locator("a[href]").First;
            var href = await ReadProductFieldAsync(context, $"listing.card[{i}].href", "a[href]", () => anchor.GetAttributeAsync("href"));
            var name = (await ReadProductFieldAsync(context, $"listing.card[{i}].name", ".product__name", () => card.Locator(".product__name").First.InnerTextAsync())).Trim();
            var price = ParsePrice(await ReadProductFieldAsync(context, $"listing.card[{i}].price", ".product__price--show", () => card.Locator(".product__price--show").First.InnerTextAsync()));
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name) || price <= 0)
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
            var url = new Uri(CategoryUri, href);
            if (!approvedListing.Contains(url))
            {
                LogDeniedProductUrl(context, href, null);
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);
            }
            var imageLocator = card.Locator("img").First;
            var image = await ReadProductFieldAsync(context, $"listing.card[{i}].image.src", "img[src]", () => imageLocator.GetAttributeAsync("src"))
                ?? await ReadProductFieldAsync(context, $"listing.card[{i}].image.data-src", "img[data-src]", () => imageLocator.GetAttributeAsync("data-src"));
            var imageUrl = image is null ? null : new Uri(CategoryUri, image).ToString();
            if (imageUrl is not null && !CellphoneSRequestGuard.IsAllowed(new Uri(imageUrl))) imageUrl = null;
            var original = ParsePriceOrNull(await OptionalProductTextAsync(context, $"listing.card[{i}].original-price", ".product__price--through", card.Locator(".product__price--through").First));
            var text = (await ReadProductFieldAsync(context, $"listing.card[{i}].text", ".product-info", () => card.InnerTextAsync())).ToLowerInvariant();
            var stock = text.Contains("sắp về hàng") || text.Contains("sắp về") ? "coming_soon" : text.Contains("hết hàng") ? "out_of_stock" : text.Contains("còn hàng") ? "in_stock" : "unknown";
            var specs = ParseSpecifications(await ReadProductFieldAsync(context, $"listing.card[{i}].specifications", ".product-info", () => card.InnerTextAsync()));
            categoryProducts.Add(new CellphoneSCategoryProduct(name, price, url, imageUrl, original, specs, stock));
            await Task.Delay(500, ct);
        }

        var rows = new List<ScrapedOffer>(categoryProducts.Count);
        foreach (var product in categoryProducts)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDisallowed(robotsText, product.Url.AbsolutePath))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.RobotsPolicyDenied);

            context.Stage = CatalogReadStage.Navigation;
            activeCardHref = product.Url.ToString();
            var detailResponse = await page.GotoAsync(product.Url.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
            context.Stage = CatalogReadStage.AccessCheck;
            var detailText = (await page.Locator("body").InnerTextAsync()).ToLowerInvariant();
            BhxPlaywrightCatalogReader.EnsureAccessible(detailResponse?.Status, page.Url, detailText);
            if (!TryValidateDetailUrl(context, approvedListing, product.Url, page.Url, out var detailUri))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);

            context.Stage = CatalogReadStage.ProductFields;
            var sku = await ReadPublishedProductIdAsync(context, page, approvedListing, product.Url, detailUri, product.PriceVnd);
            rows.Add(new ScrapedOffer(sku, product.Name, "Apple", product.PriceVnd, product.Url.ToString(), product.ImageUrl,
                product.OriginalPriceVnd, product.Specifications, product.StockStatus));
        }
        return rows;
    }

    internal async Task HandleRouteAsync(IRoute route, CatalogReadContext context, string? activeCardHref,
        Func<CellphoneSListingProvenance?> provenance, SemaphoreSlim gate, CancellationToken ct)
    {
        if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) || !IsRouteAllowed(uri, provenance()))
        {
            if (activeCardHref is not null && route.Request.IsNavigationRequest && route.Request.Frame.ParentFrame is null)
                LogDeniedProductUrl(context, activeCardHref, route.Request.Url);
            await route.AbortAsync();
            return;
        }
        await gate.WaitAsync(ct);
        try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); await route.ContinueAsync(); }
        finally { gate.Release(); }
    }

    internal bool TryValidateDetailUrl(CatalogReadContext context, CellphoneSListingProvenance provenance, Uri expectedUrl, string finalUrl, out Uri detailUri)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var parsedUri) || parsedUri is null ||
            !provenance.Contains(expectedUrl) || !provenance.Contains(parsedUri) ||
            !string.Equals(CellphoneSListingProvenance.Canonicalize(parsedUri), CellphoneSListingProvenance.Canonicalize(expectedUrl), StringComparison.Ordinal))
        {
            detailUri = expectedUrl;
            LogDeniedProductUrl(context, expectedUrl.ToString(), finalUrl);
            return false;
        }
        detailUri = parsedUri;
        return true;
    }

    private static bool IsRouteAllowed(Uri uri, CellphoneSListingProvenance? provenance) =>
        CellphoneSRequestGuard.IsAllowedProductPage(uri) || provenance?.Contains(uri) == true;

    internal void LogDeniedProductUrl(CatalogReadContext context, string? cardHref, string? finalUri)
    {
        logger.LogWarning("CellphoneS product URL denied. Source={Source}; JobId={JobId}; Stage={Stage}; CardHref={CardHref}; CardHrefPath={CardHrefPath}; FinalRequestUri={FinalRequestUri}; FinalRequestPath={FinalRequestPath}",
            context.Source, context.JobId, context.Stage,
            CellphoneSRequestGuard.SafeUrlForDiagnostics(cardHref, CategoryUri),
            CellphoneSRequestGuard.SafePathForDiagnostics(cardHref, CategoryUri),
            finalUri is null ? "[not-observed]" : CellphoneSRequestGuard.SafeUrlForDiagnostics(finalUri, CategoryUri),
            finalUri is null ? "[not-observed]" : CellphoneSRequestGuard.SafePathForDiagnostics(finalUri, CategoryUri));
    }

    private async Task<string> ReadPublishedProductIdAsync(CatalogReadContext context, IPage page, CellphoneSListingProvenance provenance, Uri expectedUrl, Uri detailUrl, int listingPrice)
    {
        var variants = page.Locator("li.item-variant[data-product-id]");
        var count = await ReadProductFieldAsync(context, "detail.variants.count", "li.item-variant[data-product-id]", () => variants.CountAsync());
        if (count is < 1 or > 100) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);

        var rows = new List<CellphoneSProductVariant>(count);
        for (var i = 0; i < count; i++)
        {
            var variant = variants.Nth(i);
            var id = await ReadProductFieldAsync(context, $"detail.variant[{i}].product-id", "li.item-variant[data-product-id]", () => variant.GetAttributeAsync("data-product-id"));
            var active = ((await ReadProductFieldAsync(context, $"detail.variant[{i}].class", "li.item-variant[data-product-id]", () => variant.GetAttributeAsync("class"))) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("active", StringComparer.Ordinal);
            var href = await ReadProductFieldAsync(context, $"detail.variant[{i}].href", "li.item-variant[data-product-id] a[href]", () => variant.Locator("a[href]").First.GetAttributeAsync("href"));
            var priceText = await OptionalProductTextAsync(context, $"detail.variant[{i}].price", ".item-variant-price", variant.Locator(".item-variant-price").First);
            var price = ParsePrice(priceText);
            rows.Add(new CellphoneSProductVariant(id, href, price, active));
        }

        return CellphoneSProductIdentity.Resolve(provenance, expectedUrl, listingPrice, rows, detailUrl);
    }

    internal async Task<T> ReadProductFieldAsync<T>(CatalogReadContext context, string operation, string selector, Func<Task<T>> read)
    {
        try { return await read(); }
        catch (TimeoutException ex)
        {
            logger.LogWarning(
                "CellphoneS Playwright product field timed out. Source={Source}; JobId={JobId}; Stage={Stage}; Operation={Operation}; Selector={Selector}; ActionTimeoutMs={ActionTimeoutMs}; ExceptionType={ExceptionType}",
                context.Source, context.JobId, context.Stage, operation, selector, ProductFieldActionTimeoutMs, ex.GetType().FullName);
            throw;
        }
    }

    internal async Task<string?> OptionalProductTextAsync(CatalogReadContext context, string operation, string selector, ILocator locator)
    {
        try { return await locator.InnerTextAsync(new() { Timeout = 1_000 }); }
        catch (TimeoutException ex)
        {
            logger.LogDebug(
                "CellphoneS optional product field was not available before its wait timed out. Source={Source}; JobId={JobId}; Stage={Stage}; Operation={Operation}; Selector={Selector}; ActionTimeoutMs=1000; ExceptionType={ExceptionType}",
                context.Source, context.JobId, context.Stage, operation, selector, ex.GetType().FullName);
            return null;
        }
        catch (PlaywrightException) { return null; }
    }

    private static HttpClient CreateRobotsClient(string proxyAddress)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true, Proxy = new WebProxy(proxyAddress) };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    internal static bool IsDisallowed(string robots, string path) => robots.Split('\n').Select(line => line.Trim()).Any(line =>
    {
        if (!line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase)) return false;
        var denied = line[(line.IndexOf(':') + 1)..].Trim();
        return denied == "/" || (denied.Length > 0 && path.StartsWith(denied, StringComparison.OrdinalIgnoreCase));
    });

    internal static int ParsePrice(string? text) => ParsePriceOrNull(text) ?? 0;
    private static int? ParsePriceOrNull(string? text)
    {
        var digits = Regex.Replace(text ?? string.Empty, "[^0-9]", string.Empty);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    internal static IReadOnlyDictionary<string, string> ParseSpecifications(string text)
    {
        var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var chip = Regex.Match(text, @"Apple\s+M\d+(?:\s+(?:Pro|Max))?", RegexOptions.IgnoreCase);
        var memory = Regex.Match(text, @"\b(?:8|16|18|24|32|36|48|64|96|128)\s?GB\b", RegexOptions.IgnoreCase);
        var storage = Regex.Match(text, @"\b(?:256|512)\s?GB\b|\b[1248]\s?TB\b", RegexOptions.IgnoreCase);
        var screen = Regex.Match(text, @"\b\d{1,2}(?:\.\d)?\s?inch\b", RegexOptions.IgnoreCase);
        if (chip.Success) specs["chip"] = chip.Value;
        if (memory.Success) specs["memory"] = memory.Value;
        if (storage.Success) specs["storage"] = storage.Value;
        if (screen.Success) specs["screen"] = screen.Value;
        return specs;
    }

}

internal sealed record CellphoneSCategoryProduct(
    string Name,
    int PriceVnd,
    Uri Url,
    string? ImageUrl,
    int? OriginalPriceVnd,
    IReadOnlyDictionary<string, string> Specifications,
    string StockStatus);

internal sealed record CellphoneSProductVariant(string? Id, string? Href, int PriceVnd, bool IsActive);

internal static class CellphoneSProductIdentity
{
    internal static string Resolve(CellphoneSListingProvenance? provenance, Uri expectedUrl, int listingPrice,
        IReadOnlyList<CellphoneSProductVariant> variants, Uri? finalUrl = null)
    {
        finalUrl ??= expectedUrl;
        if (provenance is null || !provenance.Contains(expectedUrl) || !provenance.Contains(finalUrl) ||
            !string.Equals(CellphoneSListingProvenance.Canonicalize(finalUrl), CellphoneSListingProvenance.Canonicalize(expectedUrl), StringComparison.Ordinal))
            throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);

        var listingId = GetUniqueProductId(expectedUrl);
        var finalId = GetUniqueProductId(finalUrl);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!int.TryParse(variant.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0 ||
                string.IsNullOrWhiteSpace(variant.Href) || variant.PriceVnd <= 0 || !ids.Add(variant.Id!))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);

            if (!Uri.TryCreate(expectedUrl, variant.Href, out var variantUrl) ||
                !provenance.Contains(variantUrl) ||
                !string.Equals(CellphoneSListingProvenance.Canonicalize(variantUrl), CellphoneSListingProvenance.Canonicalize(expectedUrl), StringComparison.Ordinal) ||
                !string.Equals(GetUniqueProductId(variantUrl), variant.Id, StringComparison.Ordinal))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
        }

        var active = variants.Where(variant => variant.IsActive).ToArray();
        if (active.Length != 1 || active[0].PriceVnd != listingPrice)
            throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
        if ((listingId is not null && !string.Equals(listingId, active[0].Id, StringComparison.Ordinal)) ||
            (finalId is not null && !string.Equals(finalId, active[0].Id, StringComparison.Ordinal)))
            throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
        return active[0].Id!;
    }

    private static string? GetUniqueProductId(Uri uri)
    {
        string? productId = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            if (!HasValidEscapes(encodedName))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);

            string name;
            try { name = Uri.UnescapeDataString(encodedName.Replace('+', ' ')); }
            catch (UriFormatException) { throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid); }
            if (!string.Equals(name, "product_id", StringComparison.Ordinal)) continue;

            if (productId is not null || separator < 0)
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
            var encodedValue = pair[(separator + 1)..];
            if (!HasValidEscapes(encodedValue))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
            try { productId = Uri.UnescapeDataString(encodedValue.Replace('+', ' ')); }
            catch (UriFormatException) { throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid); }
            if (!int.TryParse(productId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
        }
        return productId;
    }

    private static bool HasValidEscapes(string value) => !Regex.IsMatch(value, "%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant);
}
