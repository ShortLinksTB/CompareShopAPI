using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261110120000_AddFiles")]
public sealed class AddFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Width = table.Column<int>(type: "int", nullable: true),
                Height = table.Column<int>(type: "int", nullable: true),
                UploadedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Files", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Files_UploadedAtUtc", table: "Files", column: "UploadedAtUtc");
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'files.read') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'files.read');");
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'files.write') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'files.write');");
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId], [PermissionId]) SELECT 2, p.[Id] FROM [AdminPermissions] p WHERE p.[Key] IN (N'files.read', N'files.write') AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] rp WHERE rp.[RoleId] = 2 AND rp.[PermissionId] = p.[Id]);");
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId], [PermissionId]) SELECT 4, p.[Id] FROM [AdminPermissions] p WHERE p.[Key] = N'files.read' AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] rp WHERE rp.[RoleId] = 4 AND rp.[PermissionId] = p.[Id]);");
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId], [PermissionId]) SELECT 1, p.[Id] FROM [AdminPermissions] p WHERE p.[Key] IN (N'files.read', N'files.write') AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] rp WHERE rp.[RoleId] = 1 AND rp.[PermissionId] = p.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE rp FROM [AdminRolePermissions] rp INNER JOIN [AdminPermissions] p ON p.[Id] = rp.[PermissionId] WHERE p.[Key] IN (N'files.read', N'files.write');");
        migrationBuilder.Sql("DELETE FROM [AdminPermissions] WHERE [Key] IN (N'files.read', N'files.write');");
        migrationBuilder.DropTable(name: "Files");
    }
}
