using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

public partial class AddAdminManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "SeoTitle", table: "Products", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SeoDescription", table: "Products", type: "nvarchar(500)", maxLength: 500, nullable: true);
        migrationBuilder.CreateTable("AdminRoles", table => new { Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"), Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false) }, constraints: table => table.PrimaryKey("PK_AdminRoles", x => x.Id));
        migrationBuilder.CreateTable("AdminPermissions", table => new { Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"), Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false) }, constraints: table => table.PrimaryKey("PK_AdminPermissions", x => x.Id));
        migrationBuilder.CreateTable("AdminAuditLogs", table => new { Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"), ActorSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false), Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false), ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false), ResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true), CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false), BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true), AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true) }, constraints: table => table.PrimaryKey("PK_AdminAuditLogs", x => x.Id));
        migrationBuilder.CreateTable("AdminFeatureFlags", table => new { Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"), Key = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false), Enabled = table.Column<bool>(type: "bit", nullable: false), Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true) }, constraints: table => table.PrimaryKey("PK_AdminFeatureFlags", x => x.Id));
        migrationBuilder.CreateTable("AdminSettings", table => new { Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"), Key = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false), Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false) }, constraints: table => table.PrimaryKey("PK_AdminSettings", x => x.Id));
        migrationBuilder.CreateTable("ProductMatchings", table => new { Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"), Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false), Brand = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true), ProductId = table.Column<int>(type: "int", nullable: true), CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false), IsApproved = table.Column<bool>(type: "bit", nullable: false) }, constraints: table => { table.PrimaryKey("PK_ProductMatchings", x => x.Id); table.ForeignKey("FK_ProductMatchings_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.SetNull); });
        migrationBuilder.CreateTable("AdminRolePermissions", table => new { RoleId = table.Column<int>(type: "int", nullable: false), PermissionId = table.Column<int>(type: "int", nullable: false) }, constraints: table => { table.PrimaryKey("PK_AdminRolePermissions", x => new { x.RoleId, x.PermissionId }); table.ForeignKey("FK_AdminRolePermissions_AdminPermissions_PermissionId", x => x.PermissionId, "AdminPermissions", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_AdminRolePermissions_AdminRoles_RoleId", x => x.RoleId, "AdminRoles", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable("AdminUserRoles", table => new { UserId = table.Column<int>(type: "int", nullable: false), RoleId = table.Column<int>(type: "int", nullable: false) }, constraints: table => { table.PrimaryKey("PK_AdminUserRoles", x => new { x.UserId, x.RoleId }); table.ForeignKey("FK_AdminUserRoles_AdminRoles_RoleId", x => x.RoleId, "AdminRoles", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_AdminUserRoles_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateIndex("IX_AdminRoles_Name", "AdminRoles", "Name", unique: true);
        migrationBuilder.CreateIndex("IX_AdminPermissions_Key", "AdminPermissions", "Key", unique: true);
        migrationBuilder.CreateIndex("IX_AdminRolePermissions_PermissionId", "AdminRolePermissions", "PermissionId");
        migrationBuilder.CreateIndex("IX_AdminUserRoles_RoleId", "AdminUserRoles", "RoleId");
        migrationBuilder.CreateIndex("IX_AdminAuditLogs_CreatedAtUtc", "AdminAuditLogs", "CreatedAtUtc");
        migrationBuilder.CreateIndex("IX_AdminFeatureFlags_Key", "AdminFeatureFlags", "Key", unique: true);
        migrationBuilder.CreateIndex("IX_AdminSettings_Key", "AdminSettings", "Key", unique: true);
        migrationBuilder.CreateIndex("IX_ProductMatchings_IsApproved_CreatedAtUtc", "ProductMatchings", new[] { "IsApproved", "CreatedAtUtc" });

        var keys = new[] { "products.read", "products.create", "products.update", "products.delete", "categories.read", "categories.create", "categories.update", "categories.delete", "shops.read", "shops.create", "shops.update", "shops.delete", "offers.read", "offers.update", "offers.approve", "matching.read", "matching.approve", "sync_jobs.read", "sync_jobs.execute", "articles.read", "articles.update", "articles.publish", "seo.read", "seo.update", "users.manage", "roles.manage", "audit_logs.read", "settings.manage", "dashboard.read", "price_alerts.read_all", "feature_flags.read", "feature_flags.update", "analytics.read" };
        migrationBuilder.Sql("SET IDENTITY_INSERT [AdminRoles] ON; INSERT INTO [AdminRoles] ([Id],[Name]) VALUES (1,N'Super Admin'),(2,N'Operator'),(3,N'Editor'),(4,N'Viewer'),(5,N'Customer'); SET IDENTITY_INSERT [AdminRoles] OFF;");
        var permissionValues = string.Join(",", keys.Select((key, i) => $"({i + 1},N'{key}')"));
        migrationBuilder.Sql($"SET IDENTITY_INSERT [AdminPermissions] ON; INSERT INTO [AdminPermissions] ([Id],[Key]) VALUES {permissionValues}; SET IDENTITY_INSERT [AdminPermissions] OFF;");
        var grants = new List<object[]>();
        void Grant(int role, params int[] permissionIds) { foreach (var permissionId in permissionIds) grants.Add([role, permissionId]); }
        Grant(1, Enumerable.Range(1, keys.Length).ToArray());
        Grant(2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 29);
        Grant(3, 20, 21, 22, 23, 24);
        Grant(4, 1, 5, 9, 13, 16, 20, 23, 29, 33);
        var grantValues = string.Join(",", grants.Select(x => $"({x[0]},{x[1]})"));
        migrationBuilder.Sql($"INSERT INTO [AdminRolePermissions] ([RoleId],[PermissionId]) VALUES {grantValues};");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AdminUserRoles"); migrationBuilder.DropTable("AdminRolePermissions"); migrationBuilder.DropTable("AdminAuditLogs"); migrationBuilder.DropTable("AdminFeatureFlags"); migrationBuilder.DropTable("AdminSettings"); migrationBuilder.DropTable("ProductMatchings"); migrationBuilder.DropTable("AdminPermissions"); migrationBuilder.DropTable("AdminRoles");
        migrationBuilder.DropColumn(name: "SeoTitle", table: "Products"); migrationBuilder.DropColumn(name: "SeoDescription", table: "Products");
    }
}
