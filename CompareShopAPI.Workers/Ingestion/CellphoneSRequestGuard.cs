using System.Net;
using CompareShopAPI.Application.Ingestion;

namespace CompareShopAPI.Workers.Ingestion;

public static class CellphoneSRequestGuard
{
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "cellphones.com.vn", "www.cellphones.com.vn", "cdn.cellphones.com.vn", "cdn2.cellphones.com.vn" };

    public static bool IsAllowed(Uri? uri) => uri is { IsAbsoluteUri: true, Port: 443, UserInfo.Length: 0 } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && AllowedHosts.Contains(uri.IdnHost);

    public static bool IsAllowedProductPage(Uri? uri) => IsAllowed(uri) &&
        (uri!.IdnHost is not ("cellphones.com.vn" or "www.cellphones.com.vn") ||
         uri.AbsolutePath.StartsWith("/laptop/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath == "/robots.txt");

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || !IsAllowedProductPage(request.RequestUri)) throw new InvalidDataException("CellphoneS request is not approved.");
        var uri = request.RequestUri!;
        for (var redirects = 0; ; redirects++)
        {
            using var next = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var agent in request.Headers.UserAgent) next.Headers.UserAgent.Add(agent);
            var response = await client.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            { response.Dispose(); throw new SourceAccessBlockedException(); }
            if (!IsRedirect(response.StatusCode)) return response;
            var location = response.Headers.Location;
            if (location is null || redirects >= 8) return response;
            var destination = location.IsAbsoluteUri ? location : new Uri(uri, location);
            var status = (int)response.StatusCode;
            response.Dispose();
            if (!IsAllowedProductPage(destination)) throw new SourceRedirectDeniedException(status);
            uri = destination;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
