using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Domain.Articles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ArticleEndpointTests
{
    [Fact]
    public async Task Public_and_admin_routes_preserve_contract_statuses_and_permission_errors()
    {
        var repository = new FakeArticleRepository();
        var permissions = new FakePermissionGate();
        await using var app = new ApiFactory(signing: true, configureServices: services =>
        {
            services.RemoveAll<IArticleRepository>(); services.AddSingleton<IArticleRepository>(repository);
            services.RemoveAll<IArticlePermissionGate>(); services.AddSingleton<IArticlePermissionGate>(permissions);
            services.RemoveAll<IArticleProductLookup>(); services.AddSingleton<IArticleProductLookup, FakeProductLookup>();
        });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/articles?page=1&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/articles/article")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/articles/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/articles")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/articles/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/admin/articles", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/admin/articles/1", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/admin/articles/1/transitions/review", new ArticleTransitionInput("review"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/admin/articles/1")).StatusCode);

        permissions.Allowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/articles")).StatusCode);
        permissions.Allowed = true;
        repository.NextError = new ArticleSlugConflictException();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/admin/articles", Input())).StatusCode);
        repository.NextError = new InvalidOperationException("concurrent update");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/admin/articles/1/transitions/review", new ArticleTransitionInput("review"))).StatusCode);
    }

    private static ArticleInput Input() => new("article", "Title", "Excerpt", null, "ignored", "Author", null, 3, "Body", new(null, null, null, null), []);

    private sealed class FakePermissionGate : IArticlePermissionGate
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(Allowed);
    }

    private sealed class FakeProductLookup : IArticleProductLookup
    {
        public Task<bool> IsActive(int productId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeArticleRepository : IArticleRepository
    {
        public Exception? NextError { get; set; }
        private readonly ArticleDto dto = new(1, "article", "Title", "Excerpt", null, "subject", "Author", null, 3, "Body", ArticleStatus.Draft,
            ArticleSeo.Create(null, null, null, null, true), DateTime.UtcNow, DateTime.UtcNow, null, null, []);
        private void ThrowNext() { if (NextError is { } error) { NextError = null; throw error; } }
        public Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct) { ThrowNext(); return Task.FromResult(new ArticlePage([], page, pageSize, 0, 0)); }
        public Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct)
        {
            ThrowNext();
            return Task.FromResult<PublicArticleDto?>(slug == "article" ? new("article", "Title", "Excerpt", null, "Author", null, 3, "Body", dto.Seo, dto.UpdatedAtUtc, dto.UpdatedAtUtc, []) : null);
        }
        public Task<AdminArticlePage> AdminList(int page, int pageSize, CancellationToken ct) { ThrowNext(); return Task.FromResult(new AdminArticlePage([dto], page, pageSize, 1, 1)); }
        public Task<ArticleDto?> AdminGet(int id, CancellationToken ct) { ThrowNext(); return Task.FromResult<ArticleDto?>(id == 1 ? dto : null); }
        public Task<ArticleDto> Create(ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct) { ThrowNext(); return Task.FromResult(dto); }
        public Task<ArticleDto> Update(int id, ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct) { ThrowNext(); return Task.FromResult(dto); }
        public Task<ArticleDto?> Transition(int id, ArticleStatus target, DateTime? scheduledAtUtc, ArticleStatus? expectedStatus, string actor, DateTime nowUtc, CancellationToken ct) { ThrowNext(); return Task.FromResult<ArticleDto?>(dto); }
        public Task<IReadOnlyList<int>> DueScheduledIds(DateTime nowUtc, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
    }
}
