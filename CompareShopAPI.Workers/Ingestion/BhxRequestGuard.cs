namespace CompareShopAPI.Workers.Ingestion;

public static class BhxRequestGuard
{
    public const string AllowedHost = "bachhoaxanh.com";

    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Port: 443, UserInfo.Length: 0 } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, AllowedHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedPlaywrightRequest(string requestUrl) =>
        Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) && IsAllowed(uri);

    public static HttpClient CreateHttpClient(TimeSpan timeout) =>
        CreateHttpClient(timeout, new SocketsHttpHandler { AllowAutoRedirect = false });

    internal static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler transport)
    {
        var handler = new BhxAllowlistHttpMessageHandler
        {
            InnerHandler = transport
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get)
            throw new InvalidOperationException("BHX requests currently support GET only.");

        var uri = request.RequestUri ?? throw new InvalidOperationException("BHX request URI is required.");
        if (!IsAllowed(uri))
            throw new InvalidDataException("BHX request URI is not approved.");

        var userAgent = request.Headers.UserAgent.ToArray();
        const int maxRedirects = 10;
        for (var redirects = 0; ; redirects++)
        {
            using var nextRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var header in userAgent)
                nextRequest.Headers.UserAgent.Add(header);

            var response = await client.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            if (location is null || redirects >= maxRedirects)
                return response;

            var nextUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            response.Dispose();
            if (!IsAllowed(nextUri))
                throw new InvalidDataException("BHX redirect target is not approved.");

            uri = nextUri;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.RedirectMethod
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;
}

internal sealed class BhxAllowlistHttpMessageHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!BhxRequestGuard.IsAllowed(request.RequestUri))
            throw new InvalidDataException("BHX request URI is not approved.");

        return base.SendAsync(request, cancellationToken);
    }
}
