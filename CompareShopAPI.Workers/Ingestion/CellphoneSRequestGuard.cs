using System.Net;
using CompareShopAPI.Application.Ingestion;

namespace CompareShopAPI.Workers.Ingestion;

public static class CellphoneSRequestGuard
{
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "cellphones.com.vn", "www.cellphones.com.vn", "cdn.cellphones.com.vn", "cdn2.cellphones.com.vn" };

    public static bool IsAllowed(Uri? uri) => uri is { IsAbsoluteUri: true, Port: 443, UserInfo.Length: 0 } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && AllowedHosts.Contains(uri.IdnHost);

    public static bool IsAllowedProductPage(Uri? uri)
    {
        if (!IsAllowed(uri)) return false;
        var path = uri!.AbsolutePath;
        if (uri.IdnHost is "cdn.cellphones.com.vn" or "cdn2.cellphones.com.vn")
            return (path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)) &&
                new[] { ".avif", ".gif", ".jpeg", ".jpg", ".png", ".webp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        return path == "/robots.txt" || CellphoneSListingProvenance.IsExactListing(uri);
    }

    internal static string SafeUrlForDiagnostics(string? value, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[missing]";
        Uri? uri;
        var hasScheme = System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*:");
        if (hasScheme)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return "[invalid-uri]";
        }
        else if (baseUri is null || !Uri.TryCreate(baseUri, value, out uri)) return "[invalid-uri]";

        var safe = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
        safe.Query = string.Empty;
        return safe.Uri.GetLeftPart(UriPartial.Path);
    }

    internal static string SafePathForDiagnostics(string? value, Uri? baseUri = null)
    {
        var safeUrl = SafeUrlForDiagnostics(value, baseUri);
        if (!Uri.TryCreate(safeUrl, UriKind.Absolute, out var uri)) return safeUrl;
        return uri.AbsolutePath;
    }

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
        => await SendWithRedirectsAsync(client, request, IsAllowedProductPage, ct);

    internal static async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpClient client, HttpRequestMessage request, Func<Uri, bool> authorize, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null || !authorize(request.RequestUri)) throw new InvalidDataException("CellphoneS request is not approved.");
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
            if (!authorize(destination)) throw new SourceRedirectDeniedException(status);
            uri = destination;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
