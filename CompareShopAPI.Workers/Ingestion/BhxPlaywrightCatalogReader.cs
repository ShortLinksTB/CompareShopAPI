using System.Globalization;
using System.Text.RegularExpressions;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Ingestion;
using Microsoft.Playwright;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class BhxPlaywrightCatalogReader : ISourceCatalogReader
{
    private static readonly Uri CategoryUri = new("https://www.bachhoaxanh.com/bia");
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase) { "www.bachhoaxanh.com", "bachhoaxanh.com" };
    public async Task<IReadOnlyList<ScrapedOffer>> ReadValidatedBiaAsync(CancellationToken ct)
    {
        using var robotsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var robotsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(CategoryUri, "/robots.txt"));
        robotsRequest.Headers.UserAgent.ParseAdd("CompareShopPriceMonitor/1.0 (+https://giamha.online)");
        using var robotsResponse = await robotsClient.SendAsync(robotsRequest, ct);
        if (robotsResponse.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidDataException("BHX robots policy denied access.");
        if (robotsResponse.IsSuccessStatusCode)
        {
            var robots = await robotsResponse.Content.ReadAsStringAsync(ct);
            if (robots.Split('\n').Select(line => line.Trim()).Any(line =>
            {
                if (!line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase)) return false;
                var path = line[(line.IndexOf(':') + 1)..].Trim();
                return path is "/" or "/bia" || path.StartsWith("/bia/", StringComparison.OrdinalIgnoreCase);
            }))
                throw new InvalidDataException("BHX robots policy denies the validated category.");
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = ["--disable-dev-shm-usage"] });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { AcceptDownloads = false });
        page.SetDefaultNavigationTimeout(20_000); page.SetDefaultTimeout(8_000);
        using var requestGate = new SemaphoreSlim(1, 1);
        await page.RouteAsync("**/*", async route =>
        {
            if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host)) await route.AbortAsync();
            else
            {
                await requestGate.WaitAsync(ct);
                try { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); await route.ContinueAsync(); }
                finally { requestGate.Release(); }
            }
        });
        await page.GotoAsync(CategoryUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
        if (page.Url.Contains("captcha", StringComparison.OrdinalIgnoreCase) || page.Url.Contains("challenge", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BHX access challenge detected.");
        var pageText = (await page.Locator("body").InnerTextAsync()).ToLowerInvariant();
        if (pageText.Contains("captcha") || pageText.Contains("verify you are human") || pageText.Contains("truy cập bất thường"))
            throw new InvalidDataException("BHX access challenge detected.");
        var cards = page.Locator("[data-product-code]");
        var count = await cards.CountAsync();
        if (count is < 1 or > 500) throw new InvalidDataException("Validated BHX product selector changed.");
        var result = new List<ScrapedOffer>();
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
                throw new InvalidDataException("A BHX product card did not match the validated schema.");
            var url = new Uri(CategoryUri, href);
            if (!AllowedHosts.Contains(url.Host) || url.Scheme != "https") throw new InvalidDataException("BHX product link points to an unapproved host.");
            result.Add(new ScrapedOffer(sku, name, null, price, url.ToString()));
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        if (result.Count == 0) throw new InvalidDataException("Validated BHX product cards were not found.");
        return result;
    }
}
