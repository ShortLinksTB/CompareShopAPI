using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261116053200_AddProductImageDerivativeErrors")]
public sealed class AddProductImageDerivativeErrors : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Card600Error", table: "ProductImages", type: "nvarchar(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Detail1000Error", table: "ProductImages", type: "nvarchar(100)", maxLength: 100, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Card600Error", table: "ProductImages");
        migrationBuilder.DropColumn(name: "Detail1000Error", table: "ProductImages");
    }
}
