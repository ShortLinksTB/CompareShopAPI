using CompareShopAPI.Domain.Articles;

namespace CompareShopAPI.Application.Articles;

public static class ArticlePermissions
{
    public const string Read = "articles.read";
    public const string Create = "articles.create";
    public const string Delete = "articles.delete";
    public const string Update = "articles.update";
    public const string Publish = "articles.publish";
}

public sealed record BlogArticleInput(string Title, string Slug, string Category, string ExcerptText, string ContentMarkdown, string? CoverFileId, IReadOnlyList<string> Tags);
public sealed record BlogArticleDto(int Id, string Title, string Slug, string Category, string ExcerptText, string ContentMarkdown, Guid? CoverFileId, IReadOnlyList<string> Tags, string Status, string AuthorSubject, string AuthorName, DateTime? PublishedAtUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, long TotalViews);
public sealed record PublicBlogArticleDto(string Title, string Slug, string Category, string ExcerptText, string ContentMarkdown, Guid? CoverFileId, IReadOnlyList<string> Tags, string AuthorName, DateTime? PublishedAtUtc, DateTime UpdatedAtUtc, long TotalViews);
public sealed record BlogArticlePage(IReadOnlyList<BlogArticleDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);
public sealed record PublicBlogArticlePage(IReadOnlyList<PublicBlogArticleDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);
public sealed record ArticleAnalytics(int RangeDays, DateOnly FromDateUtc, DateOnly ThroughDateUtc, long TotalViews, double AverageViewsPerDay, DateOnly? PeakDateUtc, long PeakViews, long PreviousPeriodTotalViews, double? ChangePercent, IReadOnlyList<ArticleDailyViews> Daily);
public sealed record ArticleDailyViews(DateOnly DateUtc, long Views);

public sealed record ArticleInput(string Slug, string Title, string Excerpt, string? CoverImageUrl, string AuthorSubject,
    string AuthorName, string? AuthorAvatarUrl, int ReadingMinutes, string Content, ArticleSeoInput Seo, IReadOnlyList<int> RelatedProductIds);
public sealed record ArticleSeoInput(string? Title, string? Description, string? CanonicalUrl, string? OgImage, bool Index = true);
public sealed record ArticleTransitionInput(string Target, DateTime? ScheduledAtUtc = null);
public sealed record ArticleDto(int Id, string Slug, string Title, string Excerpt, string? CoverImageUrl, string AuthorSubject,
    string AuthorName, string? AuthorAvatarUrl, int ReadingMinutes, string Content, ArticleStatus Status,
    ArticleSeo Seo, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? PublishedAtUtc, DateTime? ScheduledAtUtc,
    IReadOnlyList<int> RelatedProductIds);
public sealed record PublicArticleDto(string Slug, string Title, string Excerpt, string? CoverImageUrl, string AuthorName,
    string? AuthorAvatarUrl, int ReadingMinutes, string Content, ArticleSeo Seo, DateTime PublishedAtUtc,
    DateTime UpdatedAtUtc, IReadOnlyList<int> RelatedProductIds);
public sealed record ArticlePage(IReadOnlyList<PublicArticleDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);
public sealed record AdminArticlePage(IReadOnlyList<ArticleDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);

public interface IArticleRepository
{
    Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct);
    Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct);
    Task<AdminArticlePage> AdminList(int page, int pageSize, CancellationToken ct);
    Task<ArticleDto?> AdminGet(int id, CancellationToken ct);
    Task<ArticleDto> Create(ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct);
    Task<ArticleDto> Update(int id, ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct);
    Task<ArticleDto?> Transition(int id, ArticleStatus target, DateTime? scheduledAtUtc, ArticleStatus? expectedStatus, string actor, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<int>> DueScheduledIds(DateTime nowUtc, int batchSize, CancellationToken ct);
}

public interface IBlogArticleRepository
{
    Task<PublicBlogArticlePage> BlogPublished(int page, int pageSize, string? category, CancellationToken ct);
    Task<PublicBlogArticleDto?> BlogPublishedBySlug(string slug, CancellationToken ct);
    Task<BlogArticlePage> BlogAdminList(int page, int pageSize, string? q, string? category, string? status, CancellationToken ct);
    Task<BlogArticleDto?> BlogAdminGet(int id, CancellationToken ct);
    Task<BlogArticleDto> BlogCreate(BlogArticleInput input, string actor, string authorName, DateTime nowUtc, CancellationToken ct);
    Task<BlogArticleDto> BlogUpdate(int id, BlogArticleInput input, DateTime nowUtc, CancellationToken ct);
    Task BlogDelete(int id, CancellationToken ct);
    Task<bool> BlogTransition(int id, bool publish, DateTime nowUtc, CancellationToken ct);
    Task RecordBlogView(string slug, DateOnly dateUtc, CancellationToken ct);
    Task<ArticleAnalytics?> BlogAnalytics(int id, int rangeDays, DateOnly todayUtc, CancellationToken ct);
}

public sealed class BlogArticleService(IBlogArticleRepository repository, IArticlePermissionGate permissions, TimeProvider clock)
{
    private static readonly string[] Categories = ["shopping-tips", "product-reviews", "market-trends"];
    public Task<PublicBlogArticlePage> PublicList(int page, int size, string? category, CancellationToken ct)
    {
        ValidatePage(page, size);
        if (category is not null && !Categories.Contains(category, StringComparer.Ordinal)) throw new ArgumentException("Unknown category.");
        return repository.BlogPublished(page, size, category, ct);
    }
    public Task<PublicBlogArticleDto?> PublicGet(string slug, CancellationToken ct) => repository.BlogPublishedBySlug(Article.NormalizeSlug(slug), ct);
    public async Task<BlogArticlePage> AdminList(string? subject, int page, int size, string? q, string? category, string? status, CancellationToken ct)
    {
        await Authorize(subject, ArticlePermissions.Read, ct); ValidatePage(page, size);
        if (q?.Trim().Length > 200 || category is not null && !Categories.Contains(category, StringComparer.Ordinal) || status is not null && status is not ("Draft" or "Published")) throw new ArgumentException("Invalid filter.");
        return await repository.BlogAdminList(page, size, q?.Trim(), category, status, ct);
    }
    public async Task<BlogArticleDto?> AdminGet(string? subject, int id, CancellationToken ct) { await Authorize(subject, ArticlePermissions.Read, ct); return await repository.BlogAdminGet(id, ct); }
    public async Task<BlogArticleDto> Create(string? subject, string? authorName, BlogArticleInput input, CancellationToken ct)
    {
        var actor = await Authorize(subject, ArticlePermissions.Create, ct); Validate(input); input = Normalize(input);
        return await repository.BlogCreate(input, actor, string.IsNullOrWhiteSpace(authorName) ? actor : authorName.Trim(), clock.GetUtcNow().UtcDateTime, ct);
    }
    public async Task<BlogArticleDto> Update(string? subject, int id, BlogArticleInput input, CancellationToken ct) { await Authorize(subject, ArticlePermissions.Update, ct); Validate(input); input = Normalize(input); return await repository.BlogUpdate(id, input, clock.GetUtcNow().UtcDateTime, ct); }
    public async Task Delete(string? subject, int id, CancellationToken ct) { await Authorize(subject, ArticlePermissions.Delete, ct); await repository.BlogDelete(id, ct); }
    public async Task Transition(string? subject, int id, bool publish, CancellationToken ct) { await Authorize(subject, ArticlePermissions.Publish, ct); if (!await repository.BlogTransition(id, publish, clock.GetUtcNow().UtcDateTime, ct)) throw new KeyNotFoundException(); }
    public Task RecordView(string slug, CancellationToken ct) => repository.RecordBlogView(Article.NormalizeSlug(slug), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct);
    public async Task<ArticleAnalytics> Analytics(string? subject, int id, int rangeDays, CancellationToken ct) { await Authorize(subject, ArticlePermissions.Read, ct); if (rangeDays is not (7 or 30 or 90)) throw new ArgumentException("rangeDays must be 7, 30, or 90."); return await repository.BlogAnalytics(id, rangeDays, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct) ?? throw new KeyNotFoundException(); }
    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct) { if (string.IsNullOrWhiteSpace(subject)) throw new CompareShopAPI.Application.Admin.AdminUnauthorizedException(); if (!await permissions.HasPermission(subject, permission, ct)) throw new CompareShopAPI.Application.Admin.AdminForbiddenException(); return subject; }
    private static void ValidatePage(int page, int size) { if (page < 1 || size is < 1 or > 100) throw new ArgumentException("Invalid page or pageSize."); }
    private static void Validate(BlogArticleInput input)
    {
        _ = Article.NormalizeSlug(input.Slug);
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 160 || string.IsNullOrWhiteSpace(input.ExcerptText) || input.ExcerptText.Trim().Length > 160 || string.IsNullOrWhiteSpace(input.ContentMarkdown) || System.Text.Encoding.UTF8.GetByteCount(input.ContentMarkdown) > 102400 || !Categories.Contains(input.Category, StringComparer.Ordinal) || input.CoverFileId is not null && !Guid.TryParse(input.CoverFileId, out _) || input.Tags is null || input.Tags.Count > 20) throw new ArgumentException("Invalid article fields. coverFileId must be a GUID or null.");
        var tags = input.Tags.Select(x => x?.Trim() ?? "").ToArray();
        if (tags.Any(x => x.Length is 0 or > 50) || tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Length) throw new ArgumentException("Invalid tags.");
    }
    private static BlogArticleInput Normalize(BlogArticleInput input) => input with
    {
        Title = input.Title.Trim(),
        Slug = Article.NormalizeSlug(input.Slug),
        Category = input.Category,
        ExcerptText = input.ExcerptText.Trim(),
        Tags = input.Tags.Select(x => x.Trim()).ToArray()
    };
}

public interface IArticlePermissionGate
{
    Task<bool> HasPermission(string subject, string permission, CancellationToken ct);
}

public interface IArticleProductLookup
{
    Task<bool> IsActive(int productId, CancellationToken ct);
}

public sealed class ArticleService(IArticleRepository articles, IArticlePermissionGate permissions, IArticleProductLookup products, TimeProvider clock)
{
    public Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        return articles.Published(page, pageSize, ct);
    }

    public Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct) => articles.PublishedBySlug(CompareShopAPI.Domain.Articles.Article.NormalizeSlug(slug), ct);

    public async Task<AdminArticlePage> AdminList(string? subject, int page, int pageSize, CancellationToken ct)
    {
        await Authorize(subject, ArticlePermissions.Read, ct); ValidatePage(page, pageSize);
        return await articles.AdminList(page, pageSize, ct);
    }

    public async Task<ArticleDto?> AdminGet(string? subject, int id, CancellationToken ct)
    {
        await Authorize(subject, ArticlePermissions.Read, ct); ValidateId(id);
        return await articles.AdminGet(id, ct);
    }

    public async Task<ArticleDto> Create(string? subject, ArticleInput input, CancellationToken ct)
    {
        var actor = await Authorize(subject, ArticlePermissions.Update, ct);
        await ValidateProducts(input.RelatedProductIds, ct);
        return await articles.Create(input with { AuthorSubject = actor }, clock.GetUtcNow().UtcDateTime, actor, ct);
    }

    public async Task<ArticleDto> Update(string? subject, int id, ArticleInput input, CancellationToken ct)
    {
        var actor = await Authorize(subject, ArticlePermissions.Update, ct); ValidateId(id); await ValidateProducts(input.RelatedProductIds, ct);
        return await articles.Update(id, input, clock.GetUtcNow().UtcDateTime, actor, ct);
    }

    public async Task<ArticleDto> Transition(string? subject, int id, string target, DateTime? scheduledAtUtc, CancellationToken ct)
    {
        var actor = await Authorize(subject, ArticlePermissions.Update, ct); ValidateId(id);
        if (!Enum.TryParse<ArticleStatus>(target, true, out var destination)) throw new ArgumentException("Unknown article transition target.", nameof(target));
        if (!Enum.IsDefined(destination)) throw new ArgumentException("Unknown article transition target.", nameof(target));
        if (destination == ArticleStatus.Published && !await permissions.HasPermission(actor, ArticlePermissions.Publish, ct))
            throw new CompareShopAPI.Application.Admin.AdminForbiddenException();
        return await TransitionCore(id, destination, scheduledAtUtc, actor, null, ct);
    }

    public Task<ArticleDto> Archive(string? subject, int id, CancellationToken ct) => Transition(subject, id, "archived", null, ct);

    public async Task<int> PublishDue(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await articles.DueScheduledIds(now, 50, ct);
        var published = 0;
        foreach (var id in ids)
        {
            if (await articles.Transition(id, ArticleStatus.Published, null, ArticleStatus.Scheduled, "system:scheduler", now, ct) is not null) published++;
        }
        return published;
    }

    private async Task<ArticleDto> TransitionCore(int id, ArticleStatus target, DateTime? scheduledAtUtc, string actor, ArticleStatus? expected, CancellationToken ct)
    {
        return await articles.Transition(id, target, scheduledAtUtc, expected, actor, clock.GetUtcNow().UtcDateTime, ct) ?? throw new KeyNotFoundException();
    }

    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new CompareShopAPI.Application.Admin.AdminUnauthorizedException();
        if (!await permissions.HasPermission(subject, permission, ct)) throw new CompareShopAPI.Application.Admin.AdminForbiddenException();
        return subject;
    }

    private async Task ValidateProducts(IReadOnlyList<int>? ids, CancellationToken ct)
    {
        var normalized = Article.NormalizeProductIds(ids ?? throw new ArgumentNullException(nameof(ids)));
        var active = await Task.WhenAll(normalized.Select(id => products.IsActive(id, ct)));
        if (active.Any(isActive => !isActive)) throw new ArgumentException("One or more related Products do not exist or are inactive.", nameof(ids));
    }
    private static void ValidateId(int id) { if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id)); }
    private static void ValidatePage(int page, int size) { if (page < 1 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
}
