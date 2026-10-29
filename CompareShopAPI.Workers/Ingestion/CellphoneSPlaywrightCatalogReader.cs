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
    private static readonly Uri CategoryUri = new("https://www.cellphones.com.vn/laptop/mac.html");
    private const string UserAgent = "CompareShopPriceMonitor/1.0 (+https://giamha.online)";
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
        page.SetDefaultNavigationTimeout(20_000); page.SetDefaultTimeout(8_000);
        using var gate = new SemaphoreSlim(1, 1);
        await page.RouteAsync("**/*", async route =>
        {
            if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) || !CellphoneSRequestGuard.IsAllowedProductPage(uri)) { await route.AbortAsync(); return; }
            await gate.WaitAsync(ct);
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); await route.ContinueAsync(); }
            finally { gate.Release(); }
        });
        context.Stage = CatalogReadStage.Navigation;
        var response = await page.GotoAsync(CategoryUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
        context.Stage = CatalogReadStage.AccessCheck;
        BhxPlaywrightCatalogReader.EnsureAccessible(response?.Status, page.Url, (await page.Locator("body").InnerTextAsync()).ToLowerInvariant());
        context.Stage = CatalogReadStage.ProductCount;
        var cards = page.Locator(".product-info");
        var count = await cards.CountAsync();
        if (count is < 1 or > 300) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductCountInvalid);
        var rows = new List<ScrapedOffer>(count);
        context.Stage = CatalogReadStage.ProductFields;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var card = cards.Nth(i);
            var anchor = card.Locator("a[href]").First;
            var href = await anchor.GetAttributeAsync("href");
            var name = (await card.Locator(".product__name").First.InnerTextAsync()).Trim();
            var sku = await card.GetAttributeAsync("data-product-id") ?? await card.GetAttributeAsync("data-sku");
            var priceText = await card.Locator(".product__price, .price").First.InnerTextAsync();
            var price = ParsePrice(priceText);
            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name) || price <= 0)
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
            var url = new Uri(CategoryUri, href);
            if (!CellphoneSRequestGuard.IsAllowedProductPage(url)) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);
            var image = await card.Locator("img").First.GetAttributeAsync("src") ?? await card.Locator("img").First.GetAttributeAsync("data-src");
            var imageUrl = image is null ? null : new Uri(CategoryUri, image).ToString();
            if (imageUrl is not null && !CellphoneSRequestGuard.IsAllowed(new Uri(imageUrl))) imageUrl = null;
            var original = ParsePriceOrNull(await OptionalTextAsync(card.Locator(".product__price-old, .price-old, del").First));
            var text = (await card.InnerTextAsync()).ToLowerInvariant();
            var stock = text.Contains("sắp về hàng") || text.Contains("sắp về") ? "coming_soon" : text.Contains("hết hàng") ? "out_of_stock" : text.Contains("còn hàng") ? "in_stock" : "unknown";
            var specs = ParseSpecifications(await card.InnerTextAsync());
            rows.Add(new ScrapedOffer(sku, name, "Apple", price, url.ToString(), imageUrl, original, specs, stock));
            await Task.Delay(500, ct);
        }
        return rows;
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

    private static async Task<string?> OptionalTextAsync(ILocator locator)
    {
        try { return await locator.InnerTextAsync(new() { Timeout = 1000 }); }
        catch (PlaywrightException) { return null; }
    }
}
