using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261116140000_AddFileLifecycleState")]
public sealed class AddFileLifecycleState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<int>(
        name: "LifecycleState", table: "Files", type: "int", nullable: false, defaultValue: 0);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "LifecycleState", table: "Files");
}
