using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261112030000_AddAuthSubject")]
public sealed class AddAuthSubject : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX [IX_Users_KeycloakSubject] ON [Users]; ALTER TABLE [Users] ALTER COLUMN [KeycloakSubject] nvarchar(200) NULL; CREATE UNIQUE INDEX [IX_Users_KeycloakSubject] ON [Users] ([KeycloakSubject]) WHERE [KeycloakSubject] IS NOT NULL;");
        migrationBuilder.AddColumn<string>(
            name: "AuthSubject",
            table: "Users",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Users_AuthSubject",
            table: "Users",
            column: "AuthSubject",
            unique: true,
            filter: "[AuthSubject] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Users_AuthSubject", table: "Users");
        migrationBuilder.DropColumn(name: "AuthSubject", table: "Users");
        migrationBuilder.Sql("UPDATE [Users] SET [KeycloakSubject] = CONCAT(N'auth-migrated-', [Id]) WHERE [KeycloakSubject] IS NULL; DROP INDEX [IX_Users_KeycloakSubject] ON [Users]; ALTER TABLE [Users] ALTER COLUMN [KeycloakSubject] nvarchar(200) NOT NULL; CREATE UNIQUE INDEX [IX_Users_KeycloakSubject] ON [Users] ([KeycloakSubject]);");
    }
}
