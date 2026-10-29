using System.Net;
using System.Text.RegularExpressions;

namespace CompareShopAPI.Workers.Ingestion;

internal sealed class CellphoneSListingProvenance
{
    internal static readonly Uri ApprovedListing = new("https://cellphones.com.vn/laptop/mac.html");
    private static readonly Regex TagPattern = new("<!--.*?-->|<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)\\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex RawTextElementPattern = new("<(script|style)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AttributePattern = new("(?<name>[a-zA-Z_:][a-zA-Z0-9:._-]*)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<plain>[^\\s>]+))", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
    private readonly HashSet<string> cardHrefs;

    private CellphoneSListingProvenance(HashSet<string> cardHrefs) => this.cardHrefs = cardHrefs;

    internal static bool TryCreate(Uri responseUri, string responseBody, out CellphoneSListingProvenance? provenance)
    {
        provenance = null;
        if (!IsExactListing(responseUri) || string.IsNullOrEmpty(responseBody)) return false;
        var hrefs = ExtractProductCardHrefs(responseBody)
            .Select(href => Uri.TryCreate(ApprovedListing, WebUtility.HtmlDecode(href.Trim()), out var resolved) ? Canonicalize(resolved) : null)
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (hrefs.Count == 0) return false;
        provenance = new CellphoneSListingProvenance(hrefs);
        return true;
    }

    internal bool Contains(Uri? candidate) => candidate is { IsAbsoluteUri: true } &&
        IsProductUri(candidate) && cardHrefs.Contains(Canonicalize(candidate));

    internal static bool IsExactListing(Uri? uri) => uri is { IsAbsoluteUri: true, Port: 443, UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(uri.IdnHost, ApprovedListing.IdnHost, StringComparison.Ordinal) &&
        string.Equals(uri.AbsolutePath, ApprovedListing.AbsolutePath, StringComparison.Ordinal);

    internal static string Canonicalize(Uri uri) => $"https://{uri.IdnHost.ToLowerInvariant()}{uri.AbsolutePath}";

    private static bool IsProductUri(Uri uri) => uri.Port == 443 && uri.UserInfo.Length == 0 &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, "cellphones.com.vn", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Length > 1;

    private static IEnumerable<string> ExtractProductCardHrefs(string html)
    {
        html = RawTextElementPattern.Replace(html, string.Empty);
        var divStack = new Stack<bool>();
        var cardDepths = new Stack<int>();
        var cardWithHref = new HashSet<int>();
        var depth = 0;
        var hrefs = new List<string>();
        foreach (Match match in TagPattern.Matches(html))
        {
            if (match.Value.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var name = match.Groups["name"].Value;
            var closing = match.Groups["close"].Success;
            if (closing)
            {
                if (name.Equals("div", StringComparison.OrdinalIgnoreCase) && divStack.Count > 0)
                {
                    if (divStack.Pop()) cardWithHref.Remove(cardDepths.Pop());
                    depth--;
                }
                continue;
            }

            var attributes = AttributePattern.Matches(match.Groups["attrs"].Value).ToDictionary(
                attribute => attribute.Groups["name"].Value,
                attribute => attribute.Groups["double"].Success ? attribute.Groups["double"].Value :
                    attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["plain"].Value,
                StringComparer.OrdinalIgnoreCase);
            var insideCard = cardDepths.Count > 0;
            if (name.Equals("div", StringComparison.OrdinalIgnoreCase))
            {
                var isCard = attributes.TryGetValue("class", out var classes) &&
                    classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("product-info", StringComparer.Ordinal);
                depth++;
                divStack.Push(isCard);
                if (isCard) cardDepths.Push(depth);
                insideCard |= isCard;
            }
            else if (insideCard && name.Equals("a", StringComparison.OrdinalIgnoreCase) && attributes.TryGetValue("href", out var href))
            {
                if (cardWithHref.Add(cardDepths.Peek())) hrefs.Add(href);
            }

            if (match.Groups["attrs"].Value.TrimEnd().EndsWith("/", StringComparison.Ordinal) || VoidElements.Contains(name))
            {
                if (name.Equals("div", StringComparison.OrdinalIgnoreCase) && divStack.Count > 0)
                {
                    if (divStack.Pop()) cardWithHref.Remove(cardDepths.Pop());
                    depth--;
                }
            }
        }
        return hrefs;
    }
}
