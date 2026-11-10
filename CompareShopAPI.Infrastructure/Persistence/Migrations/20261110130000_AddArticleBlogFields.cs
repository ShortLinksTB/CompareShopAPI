using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CompareShopAPI.Infrastructure.Persistence;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261110130000_AddArticleBlogFields")]
public sealed class AddArticleBlogFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Articles_Slug", table: "Articles");
        migrationBuilder.AlterColumn<string>(name: "Slug", table: "Articles", type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_CI_AS", oldClrType: typeof(string), oldType: "nvarchar(200)", oldMaxLength: 200);
        migrationBuilder.CreateIndex(name: "IX_Articles_Slug", table: "Articles", column: "Slug", unique: true);
        migrationBuilder.AddColumn<string>(name: "Category", table: "Articles", type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "shopping-tips");
        migrationBuilder.AddColumn<string>(name: "TagsJson", table: "Articles", type: "nvarchar(max)", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<int>(name: "CoverFileId", table: "Articles", type: "int", nullable: true);
        migrationBuilder.CreateTable(name: "ArticleViewDaily", columns: table => new
        {
            ArticleId = table.Column<int>(type: "int", nullable: false),
            DateUtc = table.Column<DateOnly>(type: "date", nullable: false),
            Views = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ArticleViewDaily", x => new { x.ArticleId, x.DateUtc });
            table.ForeignKey("FK_ArticleViewDaily_Articles_ArticleId", x => x.ArticleId, "Articles", "Id", onDelete: ReferentialAction.Restrict);
        });
        // Existing dev rows are known archived QC fixtures. Leave IDs, legacy content,
        // legacy columns, and lifecycle values intact; only the additive fields receive defaults.
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key]=N'articles.create') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'articles.create'); IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key]=N'articles.delete') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'articles.delete'); INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId]) SELECT rp.[RoleId],p.[Id] FROM [AdminRolePermissions] rp JOIN [AdminPermissions] existing ON existing.[Id]=rp.[PermissionId] AND existing.[Key]=N'articles.update' JOIN [AdminPermissions] p ON p.[Key] IN (N'articles.create',N'articles.delete') WHERE NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] x WHERE x.[RoleId]=rp.[RoleId] AND x.[PermissionId]=p.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ArticleViewDaily");
        migrationBuilder.DropColumn("CoverFileId", "Articles");
        migrationBuilder.DropColumn("TagsJson", "Articles");
        migrationBuilder.DropColumn("Category", "Articles");
        migrationBuilder.DropIndex(name: "IX_Articles_Slug", table: "Articles");
        migrationBuilder.AlterColumn<string>(name: "Slug", table: "Articles", type: "nvarchar(200)", maxLength: 200, nullable: false, oldClrType: typeof(string), oldType: "nvarchar(200)", oldMaxLength: 200, oldCollation: "Latin1_General_100_CI_AS");
        migrationBuilder.CreateIndex(name: "IX_Articles_Slug", table: "Articles", column: "Slug", unique: true);
        migrationBuilder.Sql("DELETE rp FROM [AdminRolePermissions] rp JOIN [AdminPermissions] p ON p.Id=rp.PermissionId WHERE p.[Key] IN (N'articles.create',N'articles.delete'); DELETE FROM [AdminPermissions] WHERE [Key] IN (N'articles.create',N'articles.delete');");
    }
}
