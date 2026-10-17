using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CompareShopAPI.Infrastructure.Persistence;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261017070000_AddCatalog")]
public sealed class AddCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Categories", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), Slug = table.Column<string>(maxLength: 160, nullable: false), Name = table.Column<string>(maxLength: 200, nullable: false), IsActive = table.Column<bool>(nullable: false) }, constraints: table => table.PrimaryKey("PK_Categories", x => x.Id));
        migrationBuilder.CreateTable("PartnerShops", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), Slug = table.Column<string>(maxLength: 160, nullable: false), Name = table.Column<string>(maxLength: 200, nullable: false), WebsiteUrl = table.Column<string>(nullable: true), IsActive = table.Column<bool>(nullable: false) }, constraints: table => table.PrimaryKey("PK_PartnerShops", x => x.Id));
        migrationBuilder.CreateTable("Products", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), Slug = table.Column<string>(maxLength: 200, nullable: false), Name = table.Column<string>(maxLength: 300, nullable: false), Brand = table.Column<string>(maxLength: 200, nullable: true), Description = table.Column<string>(nullable: true), CategoryId = table.Column<int>(nullable: false), IsActive = table.Column<bool>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_Products", x => x.Id); table.ForeignKey("FK_Products_Categories_CategoryId", x => x.CategoryId, "Categories", "Id", onDelete: ReferentialAction.Restrict); });
        migrationBuilder.CreateTable("ProductOffers", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), ProductId = table.Column<int>(nullable: false), PartnerShopId = table.Column<int>(nullable: false), PriceVnd = table.Column<int>(nullable: false), Url = table.Column<string>(nullable: true), UpdatedAtUtc = table.Column<DateTime>(nullable: false), IsActive = table.Column<bool>(nullable: false), IsOutlier = table.Column<bool>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_ProductOffers", x => x.Id); table.ForeignKey("FK_ProductOffers_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_ProductOffers_PartnerShops_PartnerShopId", x => x.PartnerShopId, "PartnerShops", "Id", onDelete: ReferentialAction.Restrict); });
        migrationBuilder.CreateTable("SpecificationGroups", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), ProductId = table.Column<int>(nullable: false), Name = table.Column<string>(maxLength: 200, nullable: false), SortOrder = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_SpecificationGroups", x => x.Id); table.ForeignKey("FK_SpecificationGroups_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable("SpecificationItems", table => new { Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"), SpecificationGroupId = table.Column<int>(nullable: false), Name = table.Column<string>(maxLength: 200, nullable: false), Value = table.Column<string>(maxLength: 1000, nullable: false), SortOrder = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_SpecificationItems", x => x.Id); table.ForeignKey("FK_SpecificationItems_SpecificationGroups_SpecificationGroupId", x => x.SpecificationGroupId, "SpecificationGroups", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateIndex("IX_Categories_Slug", "Categories", "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_Categories_IsActive", "Categories", "IsActive");
        migrationBuilder.CreateIndex("IX_PartnerShops_Slug", "PartnerShops", "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_PartnerShops_IsActive", "PartnerShops", "IsActive");
        migrationBuilder.CreateIndex("IX_Products_Slug", "Products", "Slug", unique: true); migrationBuilder.CreateIndex("IX_Products_IsActive_CategoryId", "Products", new[] { "IsActive", "CategoryId" }); migrationBuilder.CreateIndex("IX_Products_CategoryId", "Products", "CategoryId");
        migrationBuilder.CreateIndex("IX_ProductOffers_ProductId_IsActive_IsOutlier", "ProductOffers", new[] { "ProductId", "IsActive", "IsOutlier" }); migrationBuilder.CreateIndex("IX_ProductOffers_PartnerShopId_ProductId", "ProductOffers", new[] { "PartnerShopId", "ProductId" }); migrationBuilder.CreateIndex("IX_ProductOffers_PriceVnd_UpdatedAtUtc", "ProductOffers", new[] { "PriceVnd", "UpdatedAtUtc" }); migrationBuilder.CreateIndex("IX_ProductOffers_PartnerShopId", "ProductOffers", "PartnerShopId");
        migrationBuilder.CreateIndex("IX_SpecificationGroups_ProductId", "SpecificationGroups", "ProductId"); migrationBuilder.CreateIndex("IX_SpecificationItems_SpecificationGroupId", "SpecificationItems", "SpecificationGroupId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SpecificationItems"); migrationBuilder.DropTable("ProductOffers"); migrationBuilder.DropTable("SpecificationGroups"); migrationBuilder.DropTable("Products"); migrationBuilder.DropTable("PartnerShops"); migrationBuilder.DropTable("Categories");
    }
}
