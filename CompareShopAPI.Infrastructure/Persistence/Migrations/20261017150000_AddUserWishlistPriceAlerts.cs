using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261017150000_AddUserWishlistPriceAlerts")]
public sealed class AddUserWishlistPriceAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                ExternalSubject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Users", x => x.Id));

        migrationBuilder.CreateTable(
            name: "WishlistItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<int>(type: "int", nullable: false),
                ProductId = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WishlistItems", x => x.Id);
                table.ForeignKey("FK_WishlistItems_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_WishlistItems_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PriceAlerts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<int>(type: "int", nullable: false),
                ProductId = table.Column<int>(type: "int", nullable: false),
                TargetPrice = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PriceAlerts", x => x.Id);
                table.ForeignKey("FK_PriceAlerts_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PriceAlerts_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Users_ExternalSubject", "Users", "ExternalSubject", unique: true);
        migrationBuilder.CreateIndex("IX_WishlistItems_ProductId", "WishlistItems", "ProductId");
        migrationBuilder.CreateIndex("IX_WishlistItems_UserId_ProductId", "WishlistItems", new[] { "UserId", "ProductId" }, unique: true);
        migrationBuilder.CreateIndex("IX_PriceAlerts_ProductId", "PriceAlerts", "ProductId");
        migrationBuilder.CreateIndex("IX_PriceAlerts_UserId_IsActive", "PriceAlerts", new[] { "UserId", "IsActive" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PriceAlerts");
        migrationBuilder.DropTable(name: "WishlistItems");
        migrationBuilder.DropTable(name: "Users");
    }
}
