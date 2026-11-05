using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261105170000_GrantOffersCreateToOperator")]
public partial class GrantOffersCreateToOperator : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId], [PermissionId]) SELECT 2, p.[Id] FROM [AdminPermissions] p WHERE p.[Key] = N'offers.create' AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] rp WHERE rp.[RoleId] = 2 AND rp.[PermissionId] = p.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE rp FROM [AdminRolePermissions] rp INNER JOIN [AdminPermissions] p ON p.[Id] = rp.[PermissionId] WHERE rp.[RoleId] = 2 AND p.[Key] = N'offers.create';");
    }
}
