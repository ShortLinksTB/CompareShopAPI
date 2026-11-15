using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261115090000_AddProductImages")]
public sealed class AddProductImages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductImages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProductId = table.Column<int>(type: "int", nullable: false),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                AltText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExternalUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                Card600FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Detail1000FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductImages", x => x.Id);
                table.CheckConstraint("CK_ProductImages_Source", "([FileId] IS NOT NULL AND [ExternalUrl] IS NULL) OR ([FileId] IS NULL AND [ExternalUrl] IS NOT NULL)");
                table.ForeignKey("FK_ProductImages_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_ProductImages_ProductId_SortOrder", "ProductImages", new[] { "ProductId", "SortOrder" }, unique: true);
        migrationBuilder.CreateIndex("IX_ProductImages_ProductId_FileId", "ProductImages", new[] { "ProductId", "FileId" }, unique: true, filter: "[FileId] IS NOT NULL");

        // Keep the legacy value untouched; rerunning this backfill never duplicates a product association.
        migrationBuilder.Sql("""
            INSERT INTO [ProductImages]
                ([Id], [ProductId], [SortOrder], [AltText], [FileId], [ExternalUrl], [Card600FileId], [Detail1000FileId], [CreatedAtUtc], [UpdatedAtUtc])
            SELECT NEWID(), p.[Id], 0, NULL,
                CASE WHEN LEN(p.[ImageUrl]) = 43 AND LEFT(p.[ImageUrl], 7) = N'/files/'
                     THEN TRY_CONVERT(uniqueidentifier, SUBSTRING(p.[ImageUrl], 8, 36)) END,
                CASE WHEN LEN(p.[ImageUrl]) = 43 AND LEFT(p.[ImageUrl], 7) = N'/files/'
                          AND TRY_CONVERT(uniqueidentifier, SUBSTRING(p.[ImageUrl], 8, 36)) IS NOT NULL
                     THEN NULL ELSE p.[ImageUrl] END,
                NULL, NULL, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM [Products] AS p
            WHERE p.[ImageUrl] IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM [ProductImages] AS i WHERE i.[ProductId] = p.[Id]);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "ProductImages");
}
