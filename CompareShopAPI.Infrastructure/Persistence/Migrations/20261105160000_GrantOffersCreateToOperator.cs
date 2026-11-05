using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261105160000_GrantOffersCreateToOperator")]
public partial class GrantOffersCreateToOperator : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId]) SELECT 2, permission.[Id] FROM [AdminPermissions] permission WHERE permission.[Key] = N'offers.create' AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId] = 2 AND existing.[PermissionId] = permission.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM [AdminRolePermissions] WHERE [RoleId] = 2 AND [PermissionId] IN (SELECT [Id] FROM [AdminPermissions] WHERE [Key] = N'offers.create');");
    }
}
