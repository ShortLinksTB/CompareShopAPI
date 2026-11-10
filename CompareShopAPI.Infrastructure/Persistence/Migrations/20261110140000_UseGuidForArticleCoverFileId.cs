using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261110140000_UseGuidForArticleCoverFileId")]
public sealed class UseGuidForArticleCoverFileId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [Articles] WHERE [CoverFileId] IS NOT NULL) THROW 51000, 'Cannot convert existing Article CoverFileId values to GUID references.', 1;");
        migrationBuilder.DropColumn(name: "CoverFileId", table: "Articles");
        migrationBuilder.AddColumn<Guid>(name: "CoverFileId", table: "Articles", type: "uniqueidentifier", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CoverFileId", table: "Articles");
        migrationBuilder.AddColumn<int>(name: "CoverFileId", table: "Articles", type: "int", nullable: true);
    }
}
