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

public sealed class EfArticleRepository(CompareShopDbContext db) : IArticleRepository
{
    private IQueryable<Article> Ordered() => db.Articles.AsNoTracking().OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.Id);

    public async Task<ArticlePage> Published(int page, int pageSize, CancellationToken ct)
    {
        var query = Ordered().Where(x => x.Status == ArticleStatus.Published);
        var total = await query.CountAsync(ct);
        var items = await query.Skip(Offset(page, pageSize)).Take(pageSize).Select(ToPublic).ToListAsync(ct);
        return new(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public Task<PublicArticleDto?> PublishedBySlug(string slug, CancellationToken ct) =>
        db.Articles.AsNoTracking().Where(x => x.Status == ArticleStatus.Published && x.Slug == slug).Select(ToPublic).SingleOrDefaultAsync(ct);

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
    private static readonly System.Linq.Expressions.Expression<Func<Article, PublicArticleDto>> ToPublic = x => new PublicArticleDto(x.Slug, x.Title, x.Excerpt, x.CoverImageUrl, x.AuthorName, x.AuthorAvatarUrl, x.ReadingMinutes, x.Content, x.Seo, x.PublishedAtUtc!.Value, x.UpdatedAtUtc, x.RelatedProducts.Select(p => p.ProductId).ToArray());
    private static ArticleDto ToAdmin(Article x) => new(x.Id, x.Slug, x.Title, x.Excerpt, x.CoverImageUrl, x.AuthorSubject, x.AuthorName, x.AuthorAvatarUrl, x.ReadingMinutes, x.Content, x.Status, x.Seo, x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc, x.ScheduledAtUtc, x.RelatedProducts.Select(p => p.ProductId).Order().ToArray());
}
