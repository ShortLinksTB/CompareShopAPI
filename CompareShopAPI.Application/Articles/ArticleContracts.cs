using CompareShopAPI.Domain.Articles;

namespace CompareShopAPI.Application.Articles;

public static class ArticlePermissions
{
    public const string Read = "articles.read";
    public const string Update = "articles.update";
    public const string Publish = "articles.publish";
}

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
