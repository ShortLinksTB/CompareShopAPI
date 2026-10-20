using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261020110000_AddBannedItems")]
public partial class AddBannedItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BannedItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                TargetType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                BrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                NormalizedBrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ProductId = table.Column<int>(type: "int", nullable: true),
                Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                RevokedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BannedItems", x => x.Id);
                table.CheckConstraint("CK_BannedItems_TargetShape", "([TargetType] = N'Brand' AND [BrandName] IS NOT NULL AND [NormalizedBrandName] IS NOT NULL AND [ProductId] IS NULL) OR ([TargetType] = N'Product' AND [BrandName] IS NULL AND [NormalizedBrandName] IS NULL AND [ProductId] IS NOT NULL)");
                table.ForeignKey("FK_BannedItems_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "BannedItemOutboxMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                LastError = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_BannedItemOutboxMessages", x => x.Id));

        migrationBuilder.CreateIndex("IX_BannedItems_NormalizedBrandName", "BannedItems", "NormalizedBrandName", unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Brand' AND [NormalizedBrandName] IS NOT NULL");
        migrationBuilder.CreateIndex("IX_BannedItems_ProductId", "BannedItems", "ProductId", unique: true,
            filter: "[IsActive] = 1 AND [TargetType] = N'Product' AND [ProductId] IS NOT NULL");
        migrationBuilder.CreateIndex("IX_BannedItemOutboxMessages_PublishedAtUtc_NextAttemptAtUtc_CreatedAtUtc", "BannedItemOutboxMessages",
            new[] { "PublishedAtUtc", "NextAttemptAtUtc", "CreatedAtUtc" });

        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'compliance.read') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'compliance.read');");
        migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [AdminPermissions] WHERE [Key] = N'compliance.manage') INSERT INTO [AdminPermissions] ([Key]) VALUES (N'compliance.manage');");
        migrationBuilder.Sql("INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId]) SELECT role.[Id], permission.[Id] FROM [AdminRoles] role CROSS JOIN [AdminPermissions] permission WHERE role.[Name] IN (N'Super Admin',N'Operator') AND permission.[Key] IN (N'compliance.read',N'compliance.manage') AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId] = role.[Id] AND existing.[PermissionId] = permission.[Id]);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE rp FROM [AdminRolePermissions] rp INNER JOIN [AdminPermissions] p ON p.[Id] = rp.[PermissionId] WHERE p.[Key] IN (N'compliance.read',N'compliance.manage');");
        migrationBuilder.Sql("DELETE FROM [AdminPermissions] WHERE [Key] IN (N'compliance.read',N'compliance.manage');");
        migrationBuilder.DropTable(name: "BannedItemOutboxMessages");
        migrationBuilder.DropTable(name: "BannedItems");
    }
}
