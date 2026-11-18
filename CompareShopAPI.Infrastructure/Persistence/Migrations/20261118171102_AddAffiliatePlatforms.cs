using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

public partial class AddAffiliatePlatforms : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AffiliatePlatforms",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                NormalizedName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                HomeUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AffiliatePlatforms", x => x.Id));

        migrationBuilder.CreateIndex("IX_AffiliatePlatforms_NormalizedName", "AffiliatePlatforms", "NormalizedName", unique: true);
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.AdminPermissions', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.AdminRolePermissions', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [AdminPermissions] ([Key])
                SELECT keys.[Key] FROM (VALUES
                    (N'affiliate_platforms.read'),(N'affiliate_platforms.create'),
                    (N'affiliate_platforms.update'),(N'affiliate_platforms.delete')) keys([Key])
                WHERE NOT EXISTS (SELECT 1 FROM [AdminPermissions] existing WHERE existing.[Key]=keys.[Key]);
                INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId])
                SELECT roles.[RoleId], permissions.[Id]
                FROM (VALUES (1),(2)) roles([RoleId])
                CROSS JOIN [AdminPermissions] permissions
                WHERE permissions.[Key] IN (N'affiliate_platforms.read',N'affiliate_platforms.create',N'affiliate_platforms.update',N'affiliate_platforms.delete')
                  AND EXISTS (SELECT 1 FROM [AdminRoles] role WHERE role.[Id]=roles.[RoleId])
                  AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId]=roles.[RoleId] AND existing.[PermissionId]=permissions.[Id]);
                INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId])
                SELECT 4, permissions.[Id] FROM [AdminPermissions] permissions
                WHERE permissions.[Key]=N'affiliate_platforms.read'
                  AND EXISTS (SELECT 1 FROM [AdminRoles] role WHERE role.[Id]=4)
                  AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId]=4 AND existing.[PermissionId]=permissions.[Id]);
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF OBJECT_ID(N'dbo.AdminRolePermissions', N'U') IS NOT NULL DELETE rp FROM [AdminRolePermissions] rp JOIN [AdminPermissions] p ON p.[Id]=rp.[PermissionId] WHERE p.[Key] LIKE N'affiliate_platforms.%'; IF OBJECT_ID(N'dbo.AdminPermissions', N'U') IS NOT NULL DELETE FROM [AdminPermissions] WHERE [Key] LIKE N'affiliate_platforms.%';");
        migrationBuilder.DropTable("AffiliatePlatforms");
    }
}
