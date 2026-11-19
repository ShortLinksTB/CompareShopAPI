using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

public partial class RequireCatalogCountryAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM [PartnerShops] WHERE [CountryId] IS NULL)
    THROW 51001, 'Country assignment is required before completing this migration. Obtain approved environment-specific mappings for every PartnerShop and Category, map by Countries.Code, apply the reviewed mapping script, then restart.', 1;
IF EXISTS (SELECT 1 FROM [Categories] WHERE [CountryId] IS NULL)
    THROW 51002, 'Country assignment is required before completing this migration. Obtain approved environment-specific mappings for every PartnerShop and Category, map by Countries.Code, apply the reviewed mapping script, then restart.', 1;");

        migrationBuilder.AlterColumn<int>(name: "CountryId", table: "PartnerShops", type: "int", nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);
        migrationBuilder.AlterColumn<int>(name: "CountryId", table: "Categories", type: "int", nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(name: "CountryId", table: "PartnerShops", type: "int", nullable: true, oldClrType: typeof(int), oldType: "int");
        migrationBuilder.AlterColumn<int>(name: "CountryId", table: "Categories", type: "int", nullable: true, oldClrType: typeof(int), oldType: "int");
    }
}
