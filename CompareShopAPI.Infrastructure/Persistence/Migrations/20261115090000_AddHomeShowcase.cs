using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261115090000_AddHomeShowcase")]
public sealed class AddHomeShowcase : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HomeShowcaseSlots",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Snapshot = table.Column<int>(type: "int", nullable: false),
                Section = table.Column<int>(type: "int", nullable: false),
                SlotIndex = table.Column<int>(type: "int", nullable: false),
                ProductId = table.Column<int>(type: "int", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HomeShowcaseSlots", x => x.Id);
                table.CheckConstraint("CK_HomeShowcaseSlots_Snapshot", "[Snapshot] IN (0, 1)");
                table.CheckConstraint("CK_HomeShowcaseSlots_Section", "[Section] IN (0, 1)");
                table.CheckConstraint("CK_HomeShowcaseSlots_SlotIndex", "[SlotIndex] BETWEEN 1 AND 15");
            });
        migrationBuilder.CreateIndex("IX_HomeShowcaseSlots_Snapshot_Section_SlotIndex", "HomeShowcaseSlots", new[] { "Snapshot", "Section", "SlotIndex" }, unique: true);
        migrationBuilder.CreateIndex("IX_HomeShowcaseSlots_Snapshot_Section_ProductId", "HomeShowcaseSlots", new[] { "Snapshot", "Section", "ProductId" }, unique: true, filter: "[ProductId] IS NOT NULL");
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL
               AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id WHERE c.object_id=OBJECT_ID(N'dbo.Products') AND c.name=N'Id' AND t.name=N'int')
               AND EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID(N'dbo.Products') AND i.is_unique=1 AND ic.key_ordinal=1 AND c.name=N'Id' AND (SELECT COUNT(*) FROM sys.index_columns keys WHERE keys.object_id=i.object_id AND keys.index_id=i.index_id AND keys.key_ordinal>0)=1)
                ALTER TABLE [HomeShowcaseSlots] ADD CONSTRAINT [FK_HomeShowcaseSlots_Products_ProductId]
                    FOREIGN KEY ([ProductId]) REFERENCES [dbo].[Products]([Id]) ON DELETE SET NULL;
            """);

        migrationBuilder.CreateTable(
            name: "HomeShowcasePreviewTokens",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_HomeShowcasePreviewTokens", x => x.Id));
        migrationBuilder.CreateIndex("IX_HomeShowcasePreviewTokens_TokenHash", "HomeShowcasePreviewTokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_HomeShowcasePreviewTokens_ActorSubject_CreatedAtUtc", "HomeShowcasePreviewTokens", new[] { "ActorSubject", "CreatedAtUtc" });
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.AdminPermissions', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.AdminRolePermissions', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [AdminPermissions] ([Key])
                SELECT keys.[Key] FROM (VALUES (N'home_showcase.read'),(N'home_showcase.update'),(N'home_showcase.publish')) keys([Key])
                WHERE NOT EXISTS (SELECT 1 FROM [AdminPermissions] existing WHERE existing.[Key]=keys.[Key]);
                INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId])
                SELECT roles.[RoleId], permissions.[Id]
                FROM (VALUES (1),(2)) roles([RoleId])
                CROSS JOIN [AdminPermissions] permissions
                WHERE permissions.[Key] IN (N'home_showcase.read',N'home_showcase.update',N'home_showcase.publish')
                  AND EXISTS (SELECT 1 FROM [AdminRoles] role WHERE role.[Id]=roles.[RoleId])
                  AND NOT EXISTS (SELECT 1 FROM [AdminRolePermissions] existing WHERE existing.[RoleId]=roles.[RoleId] AND existing.[PermissionId]=permissions.[Id]);
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF OBJECT_ID(N'dbo.AdminRolePermissions', N'U') IS NOT NULL DELETE rp FROM [AdminRolePermissions] rp JOIN [AdminPermissions] p ON p.[Id]=rp.[PermissionId] WHERE p.[Key] IN (N'home_showcase.read',N'home_showcase.update',N'home_showcase.publish');");
        migrationBuilder.Sql("IF OBJECT_ID(N'dbo.AdminPermissions', N'U') IS NOT NULL DELETE FROM [AdminPermissions] WHERE [Key] IN (N'home_showcase.read',N'home_showcase.update',N'home_showcase.publish');");
        migrationBuilder.DropTable(name: "HomeShowcasePreviewTokens");
        migrationBuilder.DropTable(name: "HomeShowcaseSlots");
    }
}
