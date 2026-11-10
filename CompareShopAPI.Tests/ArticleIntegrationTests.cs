using CompareShopAPI.Application.Articles;
using CompareShopAPI.Domain.Articles;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Articles;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.UserCommerce;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class ArticleIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Blog_article_crud_publication_views_analytics_and_delete_use_additive_schema()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopBlog_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            var repository = new EfArticleRepository(db);
            var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            var coverFileId = Guid.Parse("f74dc3d5-df58-4b99-b118-3307277dfa56");
            var input = new BlogArticleInput("Blog title", " My-Blog ", "shopping-tips", "Excerpt", "# Markdown", coverFileId.ToString(), ["tag"]);
            var created = await repository.BlogCreate(input, "subject", "Author", now, default);
            Assert.Equal("my-blog", created.Slug);
            Assert.Equal("Draft", created.Status);
            Assert.Equal(coverFileId, created.CoverFileId);
            Assert.Null(await repository.BlogPublishedBySlug("my-blog", default));
            await Assert.ThrowsAsync<ArticleSlugConflictException>(() => repository.BlogCreate(input with { Slug = "MY-BLOG" }, "subject", "Author", now, default));
            var draftList = await repository.BlogAdminList(1, 20, "Blog", "shopping-tips", "Draft", default);
            Assert.Single(draftList.Items);
            var updatedCoverFileId = Guid.Parse("a89a3f5d-91a4-4a89-bbc9-890478bd9d23");
            var updated = await repository.BlogUpdate(created.Id, input with { Title = "Updated title", CoverFileId = updatedCoverFileId.ToString(), Tags = ["second"] }, now.AddMinutes(1), default);
            Assert.Equal("Updated title", updated.Title);
            Assert.Equal(updatedCoverFileId, updated.CoverFileId);
            Assert.Equal("second", Assert.Single(updated.Tags));
            Assert.NotNull(await repository.BlogAdminGet(created.Id, default));
            Assert.Null(await repository.BlogAnalytics(int.MaxValue, 7, DateOnly.FromDateTime(now), default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.RecordBlogView("missing", DateOnly.FromDateTime(now), default));

            await repository.BlogTransition(created.Id, true, now.AddMinutes(2), default);
            Assert.Equal(1, (await repository.BlogPublished(1, 20, null, default)).TotalItems);
            Assert.Equal(1, (await repository.BlogPublished(1, 20, "shopping-tips", default)).TotalItems);
            Assert.NotNull(await repository.BlogPublishedBySlug("my-blog", default));
            await repository.RecordBlogView("my-blog", DateOnly.FromDateTime(now), default);
            await repository.RecordBlogView("my-blog", DateOnly.FromDateTime(now), default);
            await repository.RecordBlogView("my-blog", DateOnly.FromDateTime(now.AddDays(-7)), default);
            var analytics = await repository.BlogAnalytics(created.Id, 7, DateOnly.FromDateTime(now), default);
            Assert.NotNull(analytics);
            Assert.Equal(2L, analytics.TotalViews);
            Assert.Equal(1L, analytics.PreviousPeriodTotalViews);
            Assert.Equal(7, analytics.Daily.Count);
            Assert.Equal(2L, analytics.PeakViews);

            await repository.BlogTransition(created.Id, false, now.AddMinutes(1), default);
            Assert.Null(await repository.BlogPublishedBySlug("my-blog", default));
            await repository.BlogDelete(created.Id, default);
            Assert.Empty(await db.ArticleViewDaily.ToListAsync());
            Assert.Null(await repository.BlogAdminGet(created.Id, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.BlogDelete(created.Id, default));
            Assert.False(await repository.BlogTransition(created.Id, true, now, default));
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Article_schema_public_visibility_scheduler_and_restrictive_product_link_work_together()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopArticles_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using var db = new CompareShopDbContext(options);
            await db.Database.MigrateAsync();
            var category = await CategoryTestData.AddLeafAsync(db, "article-test-category", "Article Test");
            var product = new Product { Slug = "article-test-product", Name = "Article Product", CategoryId = category.Id, IsActive = true };
            db.Products.Add(product); await db.SaveChangesAsync();
            var productLookup = new UserCommerceArticleProductLookup(new EfUserCommerceRepository(db));
            Assert.True(await productLookup.IsActive(product.Id, default));
            Assert.False(await productLookup.IsActive(int.MaxValue, default));

            var subject = $"article-admin-{Guid.NewGuid():N}";
            var user = new User(subject, DateTime.UtcNow);
            var role = new AdminRole { Name = $"Article Reader {Guid.NewGuid():N}" };
            db.AddRange(user, role); await db.SaveChangesAsync();
            var readPermission = await db.AdminPermissions.SingleAsync(x => x.Key == ArticlePermissions.Read);
            db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
            db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = readPermission.Id });
            await db.SaveChangesAsync();
            var permissionGate = new AdminArticlePermissionGate(new EfAdminData(db, TimeProvider.System));
            Assert.True(await permissionGate.HasPermission(subject, ArticlePermissions.Read, default));
            Assert.False(await permissionGate.HasPermission(subject, ArticlePermissions.Publish, default));
            var repository = new EfArticleRepository(db);
            var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            var article = await repository.Create(new("article-test", "Title", "Excerpt", null, "author-sub", "Author", null, 3, "Body",
                new(null, null, null, null, true), [product.Id]), now, "admin", default);
            await Assert.ThrowsAsync<ArticleSlugConflictException>(() => repository.Create(new("article-test", "Duplicate", "Excerpt", null, "author-sub", "Author", null, 3, "Body",
                new(null, null, null, null, true), []), now, "admin", default));
            db.ChangeTracker.Clear();
            Assert.Equal(0, (await repository.Published(1, 20, default)).TotalItems);
            Assert.Equal(product.Id, Assert.Single((await repository.AdminGet(article.Id, default))!.RelatedProductIds));
            article = await repository.Update(article.Id, new("article-updated", "Updated", "Updated excerpt", null, "forged-subject", "Author", null, 4, "Updated body",
                new("SEO", "Description", "https://example.test/article", null, false), [product.Id]), now, "admin", default);
            Assert.Equal("article-updated", article.Slug);
            Assert.Equal("author-sub", article.AuthorSubject);
            Assert.False(article.Seo.Index);
            Assert.Equal(1, (await repository.AdminList(1, 20, default)).TotalItems);

            await repository.Transition(article.Id, ArticleStatus.Review, null, ArticleStatus.Draft, "admin", now, default);
            await repository.Transition(article.Id, ArticleStatus.Scheduled, now.AddMinutes(1), ArticleStatus.Review, "admin", now, default);
            Assert.Null(await repository.PublishedBySlug(article.Slug, default));
            var dueAt = now.AddMinutes(1);
            Assert.Single(await repository.DueScheduledIds(dueAt, 50, default));
            await repository.Transition(article.Id, ArticleStatus.Published, null, ArticleStatus.Scheduled, "system:scheduler", dueAt, default);
            var visible = await repository.PublishedBySlug(article.Slug, default);
            Assert.NotNull(visible);
            Assert.Equal(product.Id, Assert.Single(visible.RelatedProductIds));
            Assert.Empty(await repository.DueScheduledIds(dueAt.AddMinutes(1), 50, default));

            await Assert.ThrowsAsync<SqlException>(() => db.Products.Where(x => x.Id == product.Id).ExecuteDeleteAsync());
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
