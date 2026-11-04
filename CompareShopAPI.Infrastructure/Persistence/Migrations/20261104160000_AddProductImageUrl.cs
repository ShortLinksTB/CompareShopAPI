using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261104160000_AddProductImageUrl")]
public sealed class AddProductImageUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "ImageUrl",
        table: "Products",
        type: "nvarchar(2048)",
        maxLength: 2048,
        nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "ImageUrl",
        table: "Products");
}
