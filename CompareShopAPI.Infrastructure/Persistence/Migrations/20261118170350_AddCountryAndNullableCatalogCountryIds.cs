using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

public partial class AddCountryAndNullableCatalogCountryIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Categories_Slug", table: "Categories");
        migrationBuilder.DropIndex(name: "IX_BannedItems_NormalizedBrandName", table: "BannedItems");
        migrationBuilder.DropIndex(name: "IX_BannedItems_ProductId", table: "BannedItems");

        migrationBuilder.CreateTable(
            name: "Countries",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Countries", x => x.Id));

        migrationBuilder.AddColumn<int>(name: "CountryId", table: "PartnerShops", type: "int", nullable: true);
        migrationBuilder.AddColumn<int>(name: "CountryId", table: "Categories", type: "int", nullable: true);
        migrationBuilder.AddColumn<int>(name: "CountryId", table: "BannedItems", type: "int", nullable: true);

        migrationBuilder.CreateIndex(name: "IX_Countries_Code", table: "Countries", column: "Code", unique: true);
        migrationBuilder.CreateIndex(name: "IX_PartnerShops_CountryId", table: "PartnerShops", column: "CountryId");
        migrationBuilder.CreateIndex(name: "IX_Categories_CountryId_Slug", table: "Categories", columns: new[] { "CountryId", "Slug" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_BannedItems_CountryId", table: "BannedItems", column: "CountryId");
        migrationBuilder.CreateIndex(name: "IX_BannedItems_NormalizedBrandName_CountryId", table: "BannedItems", columns: new[] { "NormalizedBrandName", "CountryId" }, unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Brand' AND [NormalizedBrandName] IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_BannedItems_ProductId_CountryId", table: "BannedItems", columns: new[] { "ProductId", "CountryId" }, unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Product' AND [ProductId] IS NOT NULL");

        migrationBuilder.AddForeignKey(name: "FK_PartnerShops_Countries_CountryId", table: "PartnerShops", column: "CountryId", principalTable: "Countries", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "FK_Categories_Countries_CountryId", table: "Categories", column: "CountryId", principalTable: "Countries", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "FK_BannedItems_Countries_CountryId", table: "BannedItems", column: "CountryId", principalTable: "Countries", principalColumn: "Id", onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'countries.read') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'countries.read');");
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'countries.manage') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'countries.manage');");
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId]) SELECT role.[Id], permission.[Id] FROM [AdminRoles] role CROSS JOIN [AdminPermissions] permission WHERE role.[Name] = N'Super Admin' AND permission.[Key] IN (N'countries.read',N'countries.manage') AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId] = role.[Id] AND existing.[PermissionId] = permission.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE rp FROM [AdminRolePermissions] rp INNER JOIN [AdminPermissions] p ON p.[Id] = rp.[PermissionId] WHERE p.[Key] IN (N'countries.read',N'countries.manage');");
        migrationBuilder.Sql("DELETE FROM [AdminPermissions] WHERE [Key] IN (N'countries.read',N'countries.manage');");
        migrationBuilder.DropForeignKey(name: "FK_PartnerShops_Countries_CountryId", table: "PartnerShops");
        migrationBuilder.DropForeignKey(name: "FK_Categories_Countries_CountryId", table: "Categories");
        migrationBuilder.DropForeignKey(name: "FK_BannedItems_Countries_CountryId", table: "BannedItems");
        migrationBuilder.DropIndex(name: "IX_Countries_Code", table: "Countries");
        migrationBuilder.DropIndex(name: "IX_PartnerShops_CountryId", table: "PartnerShops");
        migrationBuilder.DropIndex(name: "IX_Categories_CountryId_Slug", table: "Categories");
        migrationBuilder.DropIndex(name: "IX_BannedItems_CountryId", table: "BannedItems");
        migrationBuilder.DropIndex(name: "IX_BannedItems_NormalizedBrandName_CountryId", table: "BannedItems");
        migrationBuilder.DropIndex(name: "IX_BannedItems_ProductId_CountryId", table: "BannedItems");
        migrationBuilder.DropColumn(name: "CountryId", table: "PartnerShops");
        migrationBuilder.DropColumn(name: "CountryId", table: "Categories");
        migrationBuilder.DropColumn(name: "CountryId", table: "BannedItems");
        migrationBuilder.DropTable(name: "Countries");
        migrationBuilder.CreateIndex(name: "IX_Categories_Slug", table: "Categories", column: "Slug", unique: true);
        migrationBuilder.CreateIndex(name: "IX_BannedItems_NormalizedBrandName", table: "BannedItems", column: "NormalizedBrandName", unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Brand' AND [NormalizedBrandName] IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_BannedItems_ProductId", table: "BannedItems", column: "ProductId", unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Product' AND [ProductId] IS NOT NULL");
    }
}
