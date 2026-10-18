using CompareShopAPI.Application.Articles;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Articles;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ArticleTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static Article NewArticle() => new(" My-Article ", "A title", "An excerpt", null, "author-sub", "Author", null,
        3, "Article body", ArticleSeo.Create("SEO title", null, null, null, true), [], Now);

    [Fact]
    public void Slug_is_normalized_and_product_ids_must_be_positive_and_unique()
    {
        Assert.Equal("my-article", Article.NormalizeSlug(" My-Article "));
        Assert.Equal(new[] { 2, 9 }, Article.NormalizeProductIds([9, 2]));
        Assert.Throws<ArgumentException>(() => Article.NormalizeSlug("a--b"));
        Assert.Throws<ArgumentException>(() => Article.NormalizeProductIds([2, 2]));
        Assert.Throws<ArgumentException>(() => Article.NormalizeProductIds([0]));
    }

    [Fact]
    public void Lifecycle_accepts_only_the_design_transition_matrix()
    {
        var article = NewArticle();
        article.TransitionTo(ArticleStatus.Review, Now);
        article.TransitionTo(ArticleStatus.Draft, Now);
        article.TransitionTo(ArticleStatus.Review, Now);
        article.TransitionTo(ArticleStatus.Scheduled, Now, Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), article.ScheduledAtUtc);
        article.TransitionTo(ArticleStatus.Review, Now);
        article.TransitionTo(ArticleStatus.Scheduled, Now, Now.AddMinutes(1));
        article.TransitionTo(ArticleStatus.Published, Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), article.PublishedAtUtc);
        Assert.Null(article.ScheduledAtUtc);
        article.TransitionTo(ArticleStatus.Archived, Now.AddMinutes(2));
        Assert.Throws<InvalidArticleTransitionException>(() => article.TransitionTo(ArticleStatus.Review, Now.AddMinutes(3)));
        Assert.Throws<ArgumentException>(() => article.Update("x", "t", "e", null, "s", "a", null, 1, "c", article.Seo, [], Now));
    }

    [Fact]
    public void Scheduling_requires_a_future_utc_time()
    {
        var article = NewArticle(); article.TransitionTo(ArticleStatus.Review, Now);
        Assert.Throws<ArgumentException>(() => article.TransitionTo(ArticleStatus.Scheduled, Now, Now));
        Assert.Throws<ArgumentException>(() => article.TransitionTo(ArticleStatus.Scheduled, Now, DateTime.SpecifyKind(Now.AddHours(1), DateTimeKind.Unspecified)));
        article.TransitionTo(ArticleStatus.Scheduled, Now, Now.AddHours(1));
    }

    [Fact]
    public async Task Admin_publish_requires_both_update_and_publish_permissions()
    {
        var store = new FakeArticleRepository(NewArticle());
        var permissions = new FakePermissionGate(ArticlePermissions.Update);
        var service = new ArticleService(store, permissions, new FakeProductLookup(), new TestClock(Now));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Transition("admin", 1, "published", null, default));
        Assert.Equal(ArticleStatus.Draft, store.Status);
        var publishOnly = new ArticleService(store, new FakePermissionGate(ArticlePermissions.Publish), new FakeProductLookup(), new TestClock(Now));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => publishOnly.Transition("admin", 1, "published", null, default));

        permissions.Grant(ArticlePermissions.Publish);
        await Assert.ThrowsAsync<InvalidArticleTransitionException>(() => service.Transition("admin", 1, "published", null, default));
    }

    [Fact]
    public async Task Admin_reads_require_read_and_editorial_writes_require_update()
    {
        var store = new FakeArticleRepository(NewArticle());
        var permissions = new FakePermissionGate();
        var service = new ArticleService(store, permissions, new FakeProductLookup(), new TestClock(Now));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.AdminList("admin", 1, 20, default));
        await Assert.ThrowsAsync<AdminUnauthorizedException>(() => service.AdminGet(null, 1, default));
        permissions.Grant(ArticlePermissions.Read);
        Assert.Single((await service.AdminList("admin", 1, 20, default)).Items);
        Assert.NotNull(await service.AdminGet("admin", 1, default));
        Assert.Null(await service.AdminGet("admin", 999, default));
        Assert.Null(await service.PublishedBySlug(" My-Article ", default));
        Assert.Empty((await service.Published(1, 20, default)).Items);
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Create("admin", Input(), default));
        permissions.Grant(ArticlePermissions.Update);
        Assert.Equal(1, (await service.Create("admin", Input() with { AuthorSubject = "forged-subject" }, default)).Id);
        Assert.Equal("admin", store.LastInput?.AuthorSubject);
        Assert.Equal(1, (await service.Update("admin", 1, Input(), default)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("admin", Input() with { RelatedProductIds = [int.MaxValue] }, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.Published(0, 20, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Transition("admin", 1, "99", null, default));
        await service.Archive("admin", 1, default);
        Assert.Equal(ArticleStatus.Archived, store.Status);
    }

    [Fact]
    public async Task Scheduled_publication_uses_the_same_transition_and_is_idempotent()
    {
        var article = NewArticle(); article.TransitionTo(ArticleStatus.Review, Now.AddMinutes(-3));
        article.TransitionTo(ArticleStatus.Scheduled, Now.AddMinutes(-3), Now.AddMinutes(-2));
        var store = new FakeArticleRepository(article);
        var service = new ArticleService(store, new FakePermissionGate(), new FakeProductLookup(), new TestClock(Now));
        Assert.Equal(1, await service.PublishDue(default));
        Assert.Equal(ArticleStatus.Published, store.Status);
        Assert.Equal(Now, store.PublishedAt);
        Assert.Equal(0, await service.PublishDue(default));
    }

    private sealed class TestClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class FakePermissionGate(params string[] grants) : IArticlePermissionGate
    {
        private readonly HashSet<string> keys = grants.ToHashSet(StringComparer.Ordinal);
        public void Grant(string permission) => keys.Add(permission);
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(keys.Contains(permission));
    }

    private sealed class FakeProductLookup : IArticleProductLookup
    {
        public Task<bool> IsActive(int productId, CancellationToken ct) => Task.FromResult(productId != int.MaxValue);
    }

    private sealed class FakeArticleRepository(Article article) : IArticleRepository
    {
        public ArticleStatus Status => article.Status;
        public DateTime? PublishedAt => article.PublishedAtUtc;
        public ArticleInput? LastInput { get; private set; }
        public Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct) => Task.FromResult(new ArticlePage([], page, pageSize, 0, 0));
        public Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct) => Task.FromResult<PublicArticleDto?>(null);
        public Task<AdminArticlePage> AdminList(int page, int pageSize, CancellationToken ct) => Task.FromResult(new AdminArticlePage([ToDto()], page, pageSize, 1, 1));
        public Task<ArticleDto?> AdminGet(int id, CancellationToken ct) => Task.FromResult<ArticleDto?>(id == 999 ? null : ToDto());
        public Task<ArticleDto> Create(ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct) { LastInput = input; return Task.FromResult(ToDto()); }
        public Task<ArticleDto> Update(int id, ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct) { LastInput = input; return Task.FromResult(ToDto()); }
        public Task<IReadOnlyList<int>> DueScheduledIds(DateTime nowUtc, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(article.Status == ArticleStatus.Scheduled && article.ScheduledAtUtc <= nowUtc ? [1] : []);
        public Task<ArticleDto?> Transition(int id, ArticleStatus target, DateTime? scheduledAtUtc, ArticleStatus? expectedStatus, string actor, DateTime nowUtc, CancellationToken ct)
        {
            if (expectedStatus.HasValue && article.Status != expectedStatus.Value) return Task.FromResult<ArticleDto?>(null);
            if (expectedStatus == ArticleStatus.Scheduled && (article.ScheduledAtUtc is not { } scheduledAt || scheduledAt > nowUtc)) return Task.FromResult<ArticleDto?>(null);
            article.TransitionTo(target, nowUtc, scheduledAtUtc);
            return Task.FromResult<ArticleDto?>(ToDto());
        }
        private ArticleDto ToDto() => new(1, article.Slug, article.Title, article.Excerpt, article.CoverImageUrl, article.AuthorSubject,
            article.AuthorName, article.AuthorAvatarUrl, article.ReadingMinutes, article.Content, article.Status, article.Seo,
            article.CreatedAtUtc, article.UpdatedAtUtc, article.PublishedAtUtc, article.ScheduledAtUtc, []);
    }

    private static ArticleInput Input() => new("updated-article", "Title", "Excerpt", null, "author-sub", "Author", null, 3,
        "Article body", new(null, null, null, null, true), []);
}
