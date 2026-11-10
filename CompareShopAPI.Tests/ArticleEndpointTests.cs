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
            services.RemoveAll<IBlogArticleRepository>(); services.AddSingleton<IBlogArticleRepository>(new FakeBlogRepository());
            services.RemoveAll<IArticlePermissionGate>(); services.AddSingleton<IArticlePermissionGate>(permissions);
            services.RemoveAll<IArticleProductLookup>(); services.AddSingleton<IArticleProductLookup, FakeProductLookup>();
        });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/articles?page=1&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/articles?category=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/articles/article")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/articles/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/articles/missing/view", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/articles")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/articles/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/articles/2")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/admin/articles", BlogInput())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/admin/articles", BlogInput() with { Category = "unknown" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/admin/articles/1", BlogInput())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/articles/1/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/articles/1/unpublish", null)).StatusCode);
        using var analyticsResponse = await client.GetAsync("/admin/articles/1/analytics?range=30");
        Assert.Equal(HttpStatusCode.OK, analyticsResponse.StatusCode);
        Assert.Contains("\"rangeDays\":30", await analyticsResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/articles/1/analytics?rangeDays=7")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/admin/articles/1")).StatusCode);

        permissions.Allowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/articles")).StatusCode);
        permissions.Allowed = true;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/articles/article/view", null)).StatusCode);
    }

    private static ArticleInput Input() => new("article", "Title", "Excerpt", null, "ignored", "Author", null, 3, "Body", new(null, null, null, null), []);
    private static BlogArticleInput BlogInput() => new("Title", "article", "shopping-tips", "Excerpt", "Body", null, []);

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

    private sealed class FakeBlogRepository : IBlogArticleRepository
    {
        private static readonly BlogArticleDto Dto=new(1,"Title","article","shopping-tips","Excerpt","Body",null,[],"Draft","subject","Author",null,DateTime.UtcNow,DateTime.UtcNow,0);
        public Task<PublicBlogArticlePage> BlogPublished(int page,int size,string? category,CancellationToken ct)=>Task.FromResult(new PublicBlogArticlePage([],page,size,0,0));
        public Task<PublicBlogArticleDto?> BlogPublishedBySlug(string slug,CancellationToken ct)=>Task.FromResult<PublicBlogArticleDto?>(slug=="article"?new("Title","article","shopping-tips","Excerpt","Body",null,[],"Author",null,DateTime.UtcNow,0):null);
        public Task<BlogArticlePage> BlogAdminList(int page,int size,string? q,string? category,string? status,CancellationToken ct)=>Task.FromResult(new BlogArticlePage([Dto],page,size,1,1));
        public Task<BlogArticleDto?> BlogAdminGet(int id,CancellationToken ct)=>Task.FromResult<BlogArticleDto?>(id==1?Dto:null);
        public Task<BlogArticleDto> BlogCreate(BlogArticleInput input,string actor,string author,DateTime now,CancellationToken ct)=>Task.FromResult(Dto);
        public Task<BlogArticleDto> BlogUpdate(int id,BlogArticleInput input,DateTime now,CancellationToken ct)=>Task.FromResult(Dto);
        public Task BlogDelete(int id,CancellationToken ct)=>Task.CompletedTask;
        public Task<bool> BlogTransition(int id,bool publish,DateTime now,CancellationToken ct)=>Task.FromResult(true);
        public Task RecordBlogView(string slug,DateOnly date,CancellationToken ct)=>slug=="article"?Task.CompletedTask:Task.FromException(new KeyNotFoundException());
        public Task<ArticleAnalytics?> BlogAnalytics(int id,int days,DateOnly today,CancellationToken ct)=>Task.FromResult<ArticleAnalytics?>(new(days,today.AddDays(1-days),today,0,0,null,0,0,null,[]));
    }
}
