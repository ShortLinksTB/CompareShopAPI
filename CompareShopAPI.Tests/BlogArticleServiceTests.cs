using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Domain.Articles;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class BlogArticleServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Create_requires_create_permission_and_derives_author_from_subject()
    {
        var repository = new FakeRepository();
        var permissions = new PermissionGate();
        var service = new BlogArticleService(repository, permissions, new Clock(Now));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Create("editor", "Name", Input, default));
        permissions.Granted.Add(ArticlePermissions.Create);
        var created = await service.Create("editor", null, Input with { Slug = " My-Article " }, default);
        Assert.Equal("editor", repository.Actor);
        Assert.Equal("editor", repository.Author);
        Assert.Equal("My Article", created.Title);
        Assert.Equal("my-article", repository.Input?.Slug?.Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task Validates_contract_bounds_and_exact_wire_values()
    {
        var service = new BlogArticleService(new FakeRepository(), new PermissionGate(ArticlePermissions.Create), new Clock(Now));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("editor", null, Input with { Category = "Shopping-Tips" }, default));
        var invalidCover = await Assert.ThrowsAsync<ArgumentException>(() => service.Create("editor", null, Input with { CoverFileId = "not-a-guid" }, default));
        Assert.Contains("coverFileId must be a GUID or null", invalidCover.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("editor", null, Input with { Title = new string('x', 161) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("editor", null, Input with { ContentMarkdown = new string('x', 102401) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("editor", null, Input with { Tags = ["Tag", " tag "] }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublicList(0, 20, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublicList(1, 20, "unknown", default));
    }

    [Fact]
    public async Task Publish_and_delete_use_separate_permissions()
    {
        var repository = new FakeRepository();
        var permissions = new PermissionGate(ArticlePermissions.Publish);
        var service = new BlogArticleService(repository, permissions, new Clock(Now));
        await service.Transition("editor", 1, true, default);
        Assert.True(repository.Published);
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Delete("editor", 1, default));
        permissions.Granted.Add(ArticlePermissions.Delete);
        await service.Delete("editor", 1, default);
        Assert.True(repository.Deleted);
    }

    [Fact]
    public async Task Reads_updates_analytics_and_view_recording_enforce_their_contracts()
    {
        var repository = new FakeRepository();
        var permissions = new PermissionGate(ArticlePermissions.Update);
        var service = new BlogArticleService(repository, permissions, new Clock(Now));
        await Assert.ThrowsAsync<AdminUnauthorizedException>(() => service.AdminList(null, 1, 20, null, null, null, default));
        permissions.Granted.Add(ArticlePermissions.Read);
        Assert.Single((await service.AdminList("editor", 1, 20, "  blog ", "shopping-tips", "Draft", default)).Items);
        Assert.Equal("blog", repository.Search);
        Assert.NotNull(await service.AdminGet("editor", 1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AdminList("editor", 1, 20, new string('q', 201), null, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AdminList("editor", 1, 20, null, "unknown", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AdminList("editor", 1, 20, null, null, "Archived", default));
        Assert.Equal(1, (await service.Update("editor", 1, Input, default)).Id);
        permissions.Granted.Remove(ArticlePermissions.Read);
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Analytics("editor", 1, 7, default));
        permissions.Granted.Add(ArticlePermissions.Read);
        Assert.Equal(7, (await service.Analytics("editor", 1, 7, default)).RangeDays);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Analytics("editor", 1, 14, default));
        await service.RecordView(" My-Article ", default);
        Assert.Equal("my-article", repository.LastViewedSlug);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordView("bad--slug", default));
    }

    private static readonly BlogArticleInput Input = new("My Article", "my-article", "shopping-tips", "An excerpt", "# Content", null, ["tag"]);

    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero); }
    private sealed class PermissionGate(params string[] granted) : IArticlePermissionGate
    {
        public HashSet<string> Granted { get; } = granted.ToHashSet(StringComparer.Ordinal);
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(Granted.Contains(permission));
    }
    private sealed class FakeRepository : IBlogArticleRepository
    {
        public BlogArticleInput? Input { get; private set; }
        public string? Actor { get; private set; }
        public string? Author { get; private set; }
        public string? Search { get; private set; }
        public string? LastViewedSlug { get; private set; }
        public bool Published { get; private set; }
        public bool Deleted { get; private set; }
        private static BlogArticleDto Dto => new(1,"My Article","my-article","shopping-tips","An excerpt","# Content",null,["tag"],"Draft","editor","Editor",null,Now,Now,0);
        public Task<PublicBlogArticlePage> BlogPublished(int page,int pageSize,string? category,CancellationToken ct)=>Task.FromResult(new PublicBlogArticlePage([],page,pageSize,0,0));
        public Task<PublicBlogArticleDto?> BlogPublishedBySlug(string slug,CancellationToken ct)=>Task.FromResult<PublicBlogArticleDto?>(null);
        public Task<BlogArticlePage> BlogAdminList(int page,int pageSize,string? q,string? category,string? status,CancellationToken ct){Search=q;return Task.FromResult(new BlogArticlePage([Dto],page,pageSize,1,1));}
        public Task<BlogArticleDto?> BlogAdminGet(int id,CancellationToken ct)=>Task.FromResult<BlogArticleDto?>(Dto);
        public Task<BlogArticleDto> BlogCreate(BlogArticleInput input,string actor,string authorName,DateTime nowUtc,CancellationToken ct){Input=input;Actor=actor;Author=authorName;return Task.FromResult(Dto);}
        public Task<BlogArticleDto> BlogUpdate(int id,BlogArticleInput input,DateTime nowUtc,CancellationToken ct)=>Task.FromResult(Dto);
        public Task BlogDelete(int id,CancellationToken ct){Deleted=true;return Task.CompletedTask;}
        public Task<bool> BlogTransition(int id,bool publish,DateTime nowUtc,CancellationToken ct){Published=publish;return Task.FromResult(true);}
        public Task RecordBlogView(string slug,DateOnly dateUtc,CancellationToken ct){LastViewedSlug=slug;return Task.CompletedTask;}
        public Task<ArticleAnalytics?> BlogAnalytics(int id,int rangeDays,DateOnly todayUtc,CancellationToken ct)=>Task.FromResult<ArticleAnalytics?>(new(rangeDays,todayUtc.AddDays(1-rangeDays),todayUtc,0,0,null,0,0,null,[]));
    }
}
