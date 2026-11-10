using CompareShopAPI.Domain.Articles;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class ArticleBlogMigrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Blog_migration_maps_archived_qc_fixtures_to_draft_and_preserves_legacy_rows()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopArticleBlogMigration_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;

        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync("20261018112305_AddArticles");
                await db.Database.ExecuteSqlRawAsync("""
                    SET IDENTITY_INSERT [Articles] ON;
                    INSERT INTO [Articles] ([Id],[Slug],[Title],[Excerpt],[CoverImageUrl],[AuthorSubject],[AuthorName],
                        [ReadingMinutes],[Content],[Status],[SeoIndex],[CreatedAtUtc],[UpdatedAtUtc]) VALUES
                    (91001,N'qc-fixture-91001',N'QC fixture 91001',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91002,N'qc-fixture-91002',N'QC fixture 91002',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91003,N'qc-fixture-91003',N'QC fixture 91003',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91004,N'qc-fixture-91004',N'QC fixture 91004',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91005,N'qc-fixture-91005',N'QC fixture 91005',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91006,N'qc-fixture-91006',N'QC fixture 91006',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91007,N'qc-fixture-91007',N'QC fixture 91007',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91008,N'qc-fixture-91008',N'QC fixture 91008',N'Fixture excerpt',N'https://example.test/cover.jpg',N'qc-author',N'QC Author',3,N'Original fixture body',N'Archived',0,'2026-01-02T03:04:05','2026-01-02T03:04:05'),
                    (91009,N'published-control',N'Published control',N'Control excerpt',NULL,N'control-author',N'Control Author',3,N'Published content',N'Published',0,'2026-01-02T03:04:05','2026-01-02T03:04:05');
                    SET IDENTITY_INSERT [Articles] OFF;
                    """);

                await db.Database.MigrateAsync();

                var fixtures = await db.Articles.Where(article => article.Id >= 91001 && article.Id <= 91008)
                    .OrderBy(article => article.Id).ToListAsync();
                Assert.Equal(8, fixtures.Count);
                Assert.All(fixtures, article => Assert.Equal(ArticleStatus.Draft, article.Status));
                Assert.Equal(Enumerable.Range(91001, 8), fixtures.Select(article => article.Id));
                Assert.All(fixtures, article =>
                {
                    Assert.Equal("Fixture excerpt", article.Excerpt);
                    Assert.Equal("https://example.test/cover.jpg", article.CoverImageUrl);
                    Assert.Equal("Original fixture body", article.Content);
                    Assert.Equal("qc-author", article.AuthorSubject);
                });

                var publishedControl = await db.Articles.SingleAsync(article => article.Id == 91009);
                Assert.Equal(ArticleStatus.Published, publishedControl.Status);
                Assert.Equal("Published content", publishedControl.Content);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
