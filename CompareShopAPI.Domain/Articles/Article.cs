using System.Text.RegularExpressions;

namespace CompareShopAPI.Domain.Articles;

public enum ArticleStatus { Draft, Review, Scheduled, Published, Archived }
public sealed class InvalidArticleTransitionException(string message) : Exception(message);
public sealed class ArticleSlugConflictException() : Exception("An article with this slug already exists.");

public sealed record ArticleSeo(string? Title, string? Description, string? CanonicalUrl, string? OgImage, bool Index)
{
    public static ArticleSeo Create(string? title, string? description, string? canonicalUrl, string? ogImage, bool index)
    {
        if (title?.Length > 200 || description?.Length > 500 || canonicalUrl?.Length > 2048 || ogImage?.Length > 2048)
            throw new ArgumentException("SEO metadata exceeds its maximum length.");
        ValidateUrl(canonicalUrl, nameof(canonicalUrl));
        ValidateUrl(ogImage, nameof(ogImage));
        return new(title?.Trim(), description?.Trim(), canonicalUrl?.Trim(), ogImage?.Trim(), index);
    }

    private static void ValidateUrl(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("SEO URLs must use HTTP or HTTPS.", name);
    }
}

public sealed class Article
{
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private Article() { }

    public Article(string slug, string title, string excerpt, string? coverImageUrl, string authorSubject, string authorName,
        string? authorAvatarUrl, int readingMinutes, string content, ArticleSeo seo, IEnumerable<int> relatedProductIds, DateTime nowUtc)
    {
        Slug = NormalizeSlug(slug);
        ValidateContent(title, excerpt, authorSubject, authorName, readingMinutes, content);
        Title = title.Trim(); Excerpt = excerpt.Trim(); CoverImageUrl = ValidateOptionalUrl(coverImageUrl, 2048, nameof(coverImageUrl));
        AuthorSubject = authorSubject.Trim(); AuthorName = authorName.Trim(); AuthorAvatarUrl = ValidateOptionalUrl(authorAvatarUrl, 2048, nameof(authorAvatarUrl));
        ReadingMinutes = readingMinutes; Content = content; Seo = seo ?? throw new ArgumentNullException(nameof(seo));
        Status = ArticleStatus.Draft; CreatedAtUtc = AsUtc(nowUtc); UpdatedAtUtc = CreatedAtUtc;
        RelatedProducts = NormalizeProductIds(relatedProductIds).Select(id => new ArticleRelatedProduct(0, id)).ToList();
    }

    public int Id { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Excerpt { get; private set; } = string.Empty;
    public string? CoverImageUrl { get; private set; }
    public string AuthorSubject { get; private set; } = string.Empty;
    public string AuthorName { get; private set; } = string.Empty;
    public string? AuthorAvatarUrl { get; private set; }
    public int ReadingMinutes { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public ArticleStatus Status { get; private set; }
    public ArticleSeo Seo { get; private set; } = new(null, null, null, null, true);
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public DateTime? ScheduledAtUtc { get; private set; }
    public List<ArticleRelatedProduct> RelatedProducts { get; private set; } = [];

    public void Update(string slug, string title, string excerpt, string? coverImageUrl, string authorSubject, string authorName,
        string? authorAvatarUrl, int readingMinutes, string content, ArticleSeo seo, IEnumerable<int> relatedProductIds, DateTime nowUtc)
    {
        if (Status == ArticleStatus.Archived) throw new ArgumentException("Archived articles are terminal and cannot be edited.");
        Slug = NormalizeSlug(slug);
        ValidateContent(title, excerpt, authorSubject, authorName, readingMinutes, content);
        var ids = NormalizeProductIds(relatedProductIds);
        Title = title.Trim(); Excerpt = excerpt.Trim(); CoverImageUrl = ValidateOptionalUrl(coverImageUrl, 2048, nameof(coverImageUrl));
        AuthorSubject = authorSubject.Trim(); AuthorName = authorName.Trim(); AuthorAvatarUrl = ValidateOptionalUrl(authorAvatarUrl, 2048, nameof(authorAvatarUrl));
        ReadingMinutes = readingMinutes; Content = content; Seo = seo ?? throw new ArgumentNullException(nameof(seo));
        RelatedProducts = ids.Select(id => new ArticleRelatedProduct(Id, id)).ToList();
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    public void TransitionTo(ArticleStatus target, DateTime nowUtc, DateTime? scheduledAtUtc = null)
    {
        nowUtc = AsUtc(nowUtc);
        var allowed = (Status, target) is
            (ArticleStatus.Draft, ArticleStatus.Review) or
            (ArticleStatus.Review, ArticleStatus.Draft) or
            (ArticleStatus.Review, ArticleStatus.Scheduled) or
            (ArticleStatus.Scheduled, ArticleStatus.Review) or
            (ArticleStatus.Scheduled, ArticleStatus.Published) or
            (ArticleStatus.Draft, ArticleStatus.Archived) or
            (ArticleStatus.Review, ArticleStatus.Archived) or
            (ArticleStatus.Scheduled, ArticleStatus.Archived) or
            (ArticleStatus.Published, ArticleStatus.Archived);
        if (!allowed) throw new InvalidArticleTransitionException($"Transition from {Status} to {target} is not allowed.");
        if (target == ArticleStatus.Scheduled)
        {
            if (scheduledAtUtc is null || scheduledAtUtc.Value.Kind != DateTimeKind.Utc || scheduledAtUtc.Value <= nowUtc)
                throw new ArgumentException("A scheduled publication time must be in the future and UTC.", nameof(scheduledAtUtc));
            ScheduledAtUtc = scheduledAtUtc.Value;
        }
        else ScheduledAtUtc = null;
        if (target == ArticleStatus.Published) PublishedAtUtc ??= nowUtc;
        Status = target;
        UpdatedAtUtc = nowUtc;
    }

    public static string NormalizeSlug(string? slug)
    {
        slug = slug?.Trim().ToLowerInvariant() ?? string.Empty;
        if (slug.Length is < 1 or > 200 || !SlugPattern.IsMatch(slug)) throw new ArgumentException("Slug must contain lowercase letters, numbers, and single hyphens.", nameof(slug));
        return slug;
    }

    public static int[] NormalizeProductIds(IEnumerable<int>? ids)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        var result = ids.ToArray();
        if (result.Any(id => id <= 0) || result.Distinct().Count() != result.Length) throw new ArgumentException("Related Product IDs must be positive and unique.", nameof(ids));
        return result.Order().ToArray();
    }

    private static void ValidateContent(string title, string excerpt, string subject, string author, int readingMinutes, string content)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 300 || string.IsNullOrWhiteSpace(excerpt) || excerpt.Trim().Length > 1000
            || string.IsNullOrWhiteSpace(subject) || subject.Trim().Length > 200 || string.IsNullOrWhiteSpace(author) || author.Trim().Length > 200
            || readingMinutes is < 1 or > 1440 || string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Article title, excerpt, author, reading time, and content are required and must be valid.");
    }

    private static string? ValidateOptionalUrl(string? value, int maxLength, string name)
    {
        value = value?.Trim();
        if (value?.Length > maxLength) throw new ArgumentException("URL exceeds its maximum length.", name);
        if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("URL must be an absolute HTTP or HTTPS URL within the length limit.", name);
        return value;
    }

    private static DateTime AsUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified) throw new ArgumentException("Date values must be UTC.");
        return value.ToUniversalTime();
    }
}

public sealed class ArticleRelatedProduct(int articleId, int productId)
{
    public int ArticleId { get; private set; } = articleId;
    public int ProductId { get; private set; } = productId;
}
