using System.Data;
using System.Text.Json;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Articles;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Application.UserCommerce;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Articles;

public sealed class EfArticleRepository(CompareShopDbContext db) : IArticleRepository, IBlogArticleRepository
{
    private IQueryable<Article> BlogRows() => db.Articles.AsNoTracking();
    public async Task<PublicBlogArticlePage> BlogPublished(int page, int pageSize, string? category, CancellationToken ct)
    {
        var q = BlogRows().Where(x => x.Status == ArticleStatus.Published && (category == null || x.Category == category));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.Id).Skip(Offset(page,pageSize)).Take(pageSize).ToListAsync(ct);
        var items = await PublicDtos(rows, ct);
        return new(items,page,pageSize,total,(int)Math.Ceiling(total/(double)pageSize));
    }
    public async Task<PublicBlogArticleDto?> BlogPublishedBySlug(string slug, CancellationToken ct)
    {
        var row = await BlogRows().SingleOrDefaultAsync(x => x.Status == ArticleStatus.Published && x.Slug == slug, ct);
        return row is null ? null : (await PublicDtos([row], ct)).Single();
    }
    public async Task<BlogArticlePage> BlogAdminList(int page,int pageSize,string? q,string? category,string? status,CancellationToken ct)
    {
        var query=BlogRows();
        if(!string.IsNullOrEmpty(q)) query=query.Where(x=>x.Title.Contains(q)||x.Slug.Contains(q)||x.Excerpt.Contains(q));
        if(category is not null) query=query.Where(x=>x.Category==category);
        if(status is not null) query=status=="Published"?query.Where(x=>x.Status==ArticleStatus.Published):query.Where(x=>x.Status!=ArticleStatus.Published);
        var total=await query.CountAsync(ct); var rows=await query.OrderByDescending(x=>x.UpdatedAtUtc).ThenBy(x=>x.Id).Skip(Offset(page,pageSize)).Take(pageSize).ToListAsync(ct);
        return new(await AdminDtos(rows,ct),page,pageSize,total,(int)Math.Ceiling(total/(double)pageSize));
    }
    public async Task<BlogArticleDto?> BlogAdminGet(int id,CancellationToken ct) { var row=await BlogRows().SingleOrDefaultAsync(x=>x.Id==id,ct); return row is null?null:(await AdminDtos([row],ct)).Single(); }
    public async Task<BlogArticleDto> BlogCreate(BlogArticleInput input,string actor,string authorName,DateTime nowUtc,CancellationToken ct)
    {
        var article=new Article(input.Slug,input.Title,input.ExcerptText,null,actor,authorName,null,1,input.ContentMarkdown,ArticleSeo.Create(null,null,null,null,true),Array.Empty<int>(),nowUtc);
        article.UpdateBlogFields(input.Slug,input.Title,input.Category,input.ExcerptText,input.ContentMarkdown,input.CoverFileId,System.Text.Json.JsonSerializer.Serialize(input.Tags.Select(x=>x.Trim()).ToArray()),nowUtc);
        db.Articles.Add(article); try { await db.SaveChangesAsync(ct); } catch(DbUpdateException ex) when(IsUniqueViolation(ex)){db.Entry(article).State=EntityState.Detached;throw new ArticleSlugConflictException();}
        return (await AdminDtos([article],ct)).Single();
    }
    public async Task<BlogArticleDto> BlogUpdate(int id,BlogArticleInput input,DateTime nowUtc,CancellationToken ct)
    {
        var article=await db.Articles.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new KeyNotFoundException();
        article.UpdateBlogFields(input.Slug,input.Title,input.Category,input.ExcerptText,input.ContentMarkdown,input.CoverFileId,System.Text.Json.JsonSerializer.Serialize(input.Tags.Select(x=>x.Trim()).ToArray()),nowUtc);
        try{await db.SaveChangesAsync(ct);}catch(DbUpdateException ex) when(IsUniqueViolation(ex)){throw new ArticleSlugConflictException();}
        return (await AdminDtos([article],ct)).Single();
    }
    public async Task BlogDelete(int id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var article=await db.Articles.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new KeyNotFoundException();
        await db.ArticleViewDaily.Where(x=>x.ArticleId==id).ExecuteDeleteAsync(ct);
        db.Articles.Remove(article); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<bool> BlogTransition(int id,bool publish,DateTime nowUtc,CancellationToken ct)
    {
        var article=await db.Articles.SingleOrDefaultAsync(x=>x.Id==id,ct); if(article is null)return false;
        if(publish){if(article.Status==ArticleStatus.Published) throw new InvalidArticleTransitionException("Article is already published."); article.UpdateStatus(ArticleStatus.Published,nowUtc);}
        else {if(article.Status!=ArticleStatus.Published) throw new InvalidArticleTransitionException("Article is not published."); article.UpdateStatus(ArticleStatus.Draft,nowUtc);}
        await db.SaveChangesAsync(ct); return true;
    }
    public async Task RecordBlogView(string slug,DateOnly dateUtc,CancellationToken ct)
    {
        var id=await db.Articles.Where(x=>x.Status==ArticleStatus.Published&&x.Slug==slug).Select(x=>(int?)x.Id).SingleOrDefaultAsync(ct)??throw new KeyNotFoundException();
        await db.Database.ExecuteSqlInterpolatedAsync($"IF EXISTS (SELECT 1 FROM [ArticleViewDaily] WITH (UPDLOCK,HOLDLOCK) WHERE [ArticleId]={id} AND [DateUtc]={dateUtc}) UPDATE [ArticleViewDaily] SET [Views]=[Views]+1 WHERE [ArticleId]={id} AND [DateUtc]={dateUtc}; ELSE INSERT INTO [ArticleViewDaily] ([ArticleId],[DateUtc],[Views]) VALUES ({id},{dateUtc},1);",ct);
    }
    public async Task<ArticleAnalytics?> BlogAnalytics(int id,int rangeDays,DateOnly todayUtc,CancellationToken ct)
    {
        if(!await db.Articles.AnyAsync(x=>x.Id==id,ct))return null;
        var from=todayUtc.AddDays(1-rangeDays);var previousFrom=from.AddDays(-rangeDays);var through=todayUtc;
        var data=await db.ArticleViewDaily.AsNoTracking().Where(x=>x.ArticleId==id&&x.DateUtc>=previousFrom&&x.DateUtc<=through).ToDictionaryAsync(x=>x.DateUtc,x=>x.Views,ct);
        var daily=Enumerable.Range(0,rangeDays).Select(i=>new ArticleDailyViews(from.AddDays(i),data.GetValueOrDefault(from.AddDays(i)))).ToArray();
        var previous=Enumerable.Range(0,rangeDays).Sum(i=>data.GetValueOrDefault(previousFrom.AddDays(i)));var total=daily.Sum(x=>x.Views);var peak=daily.Where(x=>x.Views>0).OrderByDescending(x=>x.Views).ThenBy(x=>x.DateUtc).FirstOrDefault();
        return new(rangeDays,from,through,total,total/(double)rangeDays,peak?.DateUtc,peak?.Views??0,previous,previous==0?null:(total-previous)*100d/previous,daily);
    }
    private async Task<IReadOnlyList<BlogArticleDto>> AdminDtos(IReadOnlyList<Article> rows,CancellationToken ct)
    {
        var ids=rows.Select(x=>x.Id).ToArray();var views=await db.ArticleViewDaily.Where(x=>ids.Contains(x.ArticleId)).GroupBy(x=>x.ArticleId).Select(g=>new{Id=g.Key,Total=g.Sum(x=>x.Views)}).ToDictionaryAsync(x=>x.Id,x=>x.Total,ct);
        return rows.Select(x=>new BlogArticleDto(x.Id,x.Title,x.Slug,x.Category,x.Excerpt,x.Content,x.CoverFileId,DeserializeTags(x.TagsJson),x.Status==ArticleStatus.Published?"Published":"Draft",x.AuthorSubject,x.AuthorName,x.PublishedAtUtc,x.CreatedAtUtc,x.UpdatedAtUtc,views.GetValueOrDefault(x.Id))).ToArray();
    }
    private async Task<IReadOnlyList<PublicBlogArticleDto>> PublicDtos(IReadOnlyList<Article> rows,CancellationToken ct)
    {
        var ids=rows.Select(x=>x.Id).ToArray();var views=await db.ArticleViewDaily.Where(x=>ids.Contains(x.ArticleId)).GroupBy(x=>x.ArticleId).Select(g=>new{Id=g.Key,Total=g.Sum(x=>x.Views)}).ToDictionaryAsync(x=>x.Id,x=>x.Total,ct);
        return rows.Select(x=>new PublicBlogArticleDto(x.Title,x.Slug,x.Category,x.Excerpt,x.Content,x.CoverFileId,DeserializeTags(x.TagsJson),x.AuthorName,x.PublishedAtUtc,x.UpdatedAtUtc,views.GetValueOrDefault(x.Id))).ToArray();
    }
    private static string[] DeserializeTags(string json)=>System.Text.Json.JsonSerializer.Deserialize<string[]>(json)??[];
    private IQueryable<Article> Ordered() => db.Articles.AsNoTracking().OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.Id);

    public async Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct)
    {
        var query = Ordered().Where(x => x.Status == ArticleStatus.Published);
        var total = await query.CountAsync(ct);
        var items = await PublicProjection(query.Skip(Offset(page, pageSize)).Take(pageSize)).ToListAsync(ct);
        return new(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct) =>
        PublicProjection(db.Articles.AsNoTracking().Where(x => x.Status == ArticleStatus.Published && x.Slug == slug)).SingleOrDefaultAsync(ct);

    public async Task<AdminArticlePage> AdminList(int page, int pageSize, CancellationToken ct)
    {
        var query = db.Articles.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id);
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(Offset(page, pageSize)).Take(pageSize).Include(x => x.RelatedProducts).ToListAsync(ct);
        return new(rows.Select(ToAdmin).ToArray(), page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<ArticleDto?> AdminGet(int id, CancellationToken ct)
    {
        var row = await db.Articles.AsNoTracking().Include(x => x.RelatedProducts).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : ToAdmin(row);
    }

    public async Task<ArticleDto> Create(ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct)
    {
        var article = CreateEntity(input, nowUtc);
        db.Articles.Add(article);
        try { await SaveAudited(actor, "create", null, null, () => ToAdmin(article), nowUtc, ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { throw new ArticleSlugConflictException(); }
        return ToAdmin(article);
    }

    public async Task<ArticleDto> Update(int id, ArticleInput input, DateTime nowUtc, string actor, CancellationToken ct)
    {
        var article = await db.Articles.Include(x => x.RelatedProducts).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var before = ToAdmin(article);
        article.Update(input.Slug, input.Title, input.Excerpt, input.CoverImageUrl, article.AuthorSubject, input.AuthorName,
            input.AuthorAvatarUrl, input.ReadingMinutes, input.Content, ToSeo(input.Seo), input.RelatedProductIds, nowUtc);
        try { await SaveAudited(actor, "update", id.ToString(), before, () => ToAdmin(article), nowUtc, ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { throw new ArticleSlugConflictException(); }
        return ToAdmin(article);
    }

    public async Task<ArticleDto?> Transition(int id, ArticleStatus target, DateTime? scheduledAtUtc, ArticleStatus? expectedStatus,
        string actor, DateTime nowUtc, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var article = await db.Articles.Include(x => x.RelatedProducts).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (article is null || (expectedStatus.HasValue && article.Status != expectedStatus.Value)
            || expectedStatus == ArticleStatus.Scheduled && (article.ScheduledAtUtc is null || article.ScheduledAtUtc > nowUtc))
        { await tx.RollbackAsync(ct); return null; }
        var before = ToAdmin(article);
        article.TransitionTo(target, nowUtc, scheduledAtUtc);
        db.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = actor, Action = "transition", ResourceType = "article", ResourceId = id.ToString(), CreatedAtUtc = nowUtc, BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(ToAdmin(article)) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToAdmin(article);
    }

    public async Task<IReadOnlyList<int>> DueScheduledIds(DateTime nowUtc, int batchSize, CancellationToken ct) =>
        await db.Articles.AsNoTracking().Where(x => x.Status == ArticleStatus.Scheduled && x.ScheduledAtUtc <= nowUtc)
            .OrderBy(x => x.ScheduledAtUtc).ThenBy(x => x.Id).Take(Math.Clamp(batchSize, 1, 100)).Select(x => x.Id).ToListAsync(ct);

    private async Task SaveAudited(string actor, string action, string? id, object? before, Func<object?> after, DateTime nowUtc, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        db.AdminAuditLogs.Add(new AdminAuditLog { ActorSubject = actor, Action = action, ResourceType = "article", ResourceId = id ?? (after() as ArticleDto)?.Id.ToString(), CreatedAtUtc = nowUtc, BeforeJson = before is null ? null : JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after()) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static Article CreateEntity(ArticleInput x, DateTime nowUtc) => new(x.Slug, x.Title, x.Excerpt, x.CoverImageUrl,
        x.AuthorSubject, x.AuthorName, x.AuthorAvatarUrl, x.ReadingMinutes, x.Content, ToSeo(x.Seo), x.RelatedProductIds, nowUtc);
    private static ArticleSeo ToSeo(ArticleSeoInput x) => ArticleSeo.Create(x.Title, x.Description, x.CanonicalUrl, x.OgImage, x.Index);
    private static int Offset(int page, int size) => (int)Math.Min(int.MaxValue, (long)(page - 1) * size);
    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2601 or 2627 };
    private IQueryable<PublicArticleDto> PublicProjection(IQueryable<Article> query) => query.Select(x => new PublicArticleDto(
        x.Slug, x.Title, x.Excerpt, x.CoverImageUrl, x.AuthorName, x.AuthorAvatarUrl, x.ReadingMinutes, x.Content, x.Seo,
        x.PublishedAtUtc!.Value, x.UpdatedAtUtc,
        x.RelatedProducts.Where(relation => db.Products.Any(product => product.Id == relation.ProductId
            && !db.BannedItems.Any(ban => ban.IsActive &&
                (ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Product && ban.ProductId == product.Id
                 || ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Brand && product.Brand != null
                    && ban.NormalizedBrandName == product.Brand.Trim().ToUpper()))))
            .Select(relation => relation.ProductId).ToArray()));
    private static ArticleDto ToAdmin(Article x) => new(x.Id, x.Slug, x.Title, x.Excerpt, x.CoverImageUrl, x.AuthorSubject, x.AuthorName, x.AuthorAvatarUrl, x.ReadingMinutes, x.Content, x.Status, x.Seo, x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc, x.ScheduledAtUtc, x.RelatedProducts.Select(p => p.ProductId).Order().ToArray());
}
