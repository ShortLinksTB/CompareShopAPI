using System.Globalization;
using System.Text.RegularExpressions;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class BhxPlaywrightCatalogReader : ISourceCatalogReader
{
    private static readonly Uri CategoryUri = new("https://www.bachhoaxanh.com/bia");

    private readonly Func<HttpClient> robotsClientFactory;
    private readonly Func<Task<IPlaywright>> playwrightFactory;
    private readonly ILogger<BhxPlaywrightCatalogReader> logger;

    public BhxPlaywrightCatalogReader() : this(() => BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(8)), NullLogger<BhxPlaywrightCatalogReader>.Instance) { }

    public BhxPlaywrightCatalogReader(ILogger<BhxPlaywrightCatalogReader> logger)
        : this(() => BhxRequestGuard.CreateHttpClient(TimeSpan.FromSeconds(8)), logger) { }

    internal BhxPlaywrightCatalogReader(Func<HttpClient> robotsClientFactory)
        : this(robotsClientFactory, NullLogger<BhxPlaywrightCatalogReader>.Instance) { }

    internal BhxPlaywrightCatalogReader(Func<HttpClient> robotsClientFactory, ILogger<BhxPlaywrightCatalogReader> logger, Func<Task<IPlaywright>>? playwrightFactory = null)
    {
        this.robotsClientFactory = robotsClientFactory;
        this.logger = logger;
        this.playwrightFactory = playwrightFactory ?? Playwright.CreateAsync;
    }

    public bool Supports(string sourceKey) => sourceKey == DataSource.BhxKey;

    public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, CancellationToken ct)
    {
        if (!Supports(sourceKey) || categoryPath != "/bia") throw new InvalidDataException("BHX source category is not supported.");
        return ReadValidatedBiaAsync(ct);
    }

    public Task<IReadOnlyList<ScrapedOffer>> ReadAsync(string sourceKey, string categoryPath, int jobId, CancellationToken ct)
    {
        if (!Supports(sourceKey) || categoryPath != "/bia") throw new InvalidDataException("BHX source category is not supported.");
        return ReadWithContextAsync(jobId, ct);
    }

    public Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct) => ReadWithContextAsync(null, ct);

    private Task<IReadOnlyList<ScrapedOffer>> ReadWithContextAsync(int? jobId, CancellationToken ct)
    {
        var context = new CatalogReadContext(DataSource.BhxKey, jobId);
        return CatalogReadDiagnostics.ReadAsync(logger, () => ReadValidatedBiaCoreAsync(context, ct), context);
    }

    private async Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaCoreAsync(CatalogReadContext context, CancellationToken ct)
    {
        context.Stage = CatalogReadStage.Robots;
        using var robotsClient = robotsClientFactory();
        using var robotsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(CategoryUri, "/robots.txt"));
        robotsRequest.Headers.UserAgent.ParseAdd("CompareShopPriceMonitor/1.0 (+https://giamha.online)");
        using var robotsResponse = await BhxRequestGuard.SendWithRedirectsAsync(robotsClient, robotsRequest, ct);
        if (robotsResponse.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.TooManyRequests)
            throw new SourceAccessBlockedException();
        if (robotsResponse.IsSuccessStatusCode)
        {
            var robots = await robotsResponse.Content.ReadAsStringAsync(ct);
            if (robots.Split('\n').Select(line => line.Trim()).Any(line =>
            {
                if (!line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase)) return false;
                var path = line[(line.IndexOf(':') + 1)..].Trim();
                return path is "/" or "/bia" || path.StartsWith("/bia/", StringComparison.OrdinalIgnoreCase);
            }))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.RobotsPolicyDenied);
        }
        context.Stage = CatalogReadStage.BrowserLaunch;
        using var playwright = await playwrightFactory();
        await using var egressProxy = await BhxForwardProxy.StartAsync(ct);
        await using var browser = await playwright.Chromium.LaunchAsync(BhxBrowserLaunchOptions.Create(egressProxy.Server)).WaitAsync(ct);
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { AcceptDownloads = false });
        page.SetDefaultNavigationTimeout(20_000); page.SetDefaultTimeout(8_000);
        using var requestGate = new SemaphoreSlim(1, 1);
        await page.RouteAsync("**/*", async route =>
        {
            if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) || !BhxRequestGuard.IsAllowed(uri)) await route.AbortAsync();
            else
            {
                await requestGate.WaitAsync(ct);
                try { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); await route.ContinueAsync(); }
                finally { requestGate.Release(); }
            }
        });
        context.Stage = CatalogReadStage.Navigation;
        var navigationResponse = await page.GotoAsync(CategoryUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
        context.Stage = CatalogReadStage.AccessCheck;
        EnsureAccessible(navigationResponse?.Status, page.Url, null);
        var pageText = (await page.Locator("body").InnerTextAsync()).ToLowerInvariant();
        EnsureAccessible(null, page.Url, pageText);
        context.Stage = CatalogReadStage.ProductCount;
        var cards = page.Locator("[data-product-code]");
        var count = await cards.CountAsync();
        if (count is < 1 or > 500) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductCountInvalid);
        var result = new List<ScrapedOffer>();
        context.Stage = CatalogReadStage.ProductFields;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var card = cards.Nth(i);
            var sku = await card.GetAttributeAsync("data-product-code");
            var name = (await card.Locator("[data-product-name]").GetAttributeAsync("data-product-name").ConfigureAwait(false))?.Trim();
            var priceText = await card.Locator("[data-price]").GetAttributeAsync("data-price");
            var anchor = card.Locator("a").First;
            var href = await anchor.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name) || !int.TryParse(Regex.Replace(priceText ?? "", "[^0-9]", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var price) || price <= 0 || string.IsNullOrWhiteSpace(href))
                throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductSchemaInvalid);
            var url = new Uri(CategoryUri, href);
            if (!BhxRequestGuard.IsAllowed(url)) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.ProductUrlDenied);
            result.Add(new ScrapedOffer(sku, name, null, price, url.ToString()));
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        if (result.Count == 0) throw CatalogReadDiagnostics.InvalidData(CatalogReadFailure.EmptyCatalog);
        return result;
    }

    internal static void EnsureAccessible(int? status, string pageUrl, string? pageText)
    {
        if (status is 401 or 403 or 429) throw new SourceAccessBlockedException();
        if (pageUrl.Contains("captcha", StringComparison.OrdinalIgnoreCase) || pageUrl.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            throw new SourceAccessBlockedException();
        if (pageText is not null && (pageText.Contains("captcha") || pageText.Contains("verify you are human") || pageText.Contains("truy cập bất thường")))
            throw new SourceAccessBlockedException();
    }

}
