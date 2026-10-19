using System.Net;
using Microsoft.Playwright;

namespace CompareShopAPI.Workers.Ingestion;

public static class BhxBrowserLaunchOptions
{
    // Microsoft.Playwright 1.56.0 bundles Chromium 141; keep this major aligned when updating Playwright.
    public static readonly string DesktopUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    public static BrowserTypeLaunchOptions Create(string proxyServer)
    {
        if (!Uri.TryCreate(proxyServer, UriKind.Absolute, out var proxyUri) ||
            proxyUri.Scheme != Uri.UriSchemeHttp || proxyUri.UserInfo.Length != 0 || proxyUri.Port < 1 ||
            proxyUri.AbsolutePath != "/" || proxyUri.Query.Length != 0 || proxyUri.Fragment.Length != 0 ||
            !IPAddress.TryParse(proxyUri.Host, out var proxyAddress) || !IPAddress.IsLoopback(proxyAddress))
            throw new ArgumentException("The BHX proxy must be an HTTP loopback endpoint.", nameof(proxyServer));

        return new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--disable-dev-shm-usage",
                "--disable-quic",
                "--disable-webrtc",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                $"--user-agent={DesktopUserAgent}"
            ],
            Proxy = new Proxy
            {
                Server = proxyServer,
                Bypass = "<-loopback>"
            }
        };
    }
}
