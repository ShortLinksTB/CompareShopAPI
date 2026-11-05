using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261105150000_AddOffersCreatePermission")]
public partial class AddOffersCreatePermission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'offers.create') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'offers.create');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM [AdminRolePermissions] WHERE [PermissionId] IN (SELECT [Id] FROM [AdminPermissions] WHERE [Key] = N'offers.create'); DELETE FROM [AdminPermissions] WHERE [Key] = N'offers.create';");
    }
}
