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
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [Users] WHERE [KeycloakSubject] IS NULL) BEGIN THROW 51000, 'Cannot roll back AddAuthSubject while users without a Keycloak identity exist.', 1; END;");
        migrationBuilder.DropIndex(name: "IX_Users_AuthSubject", table: "Users");
        migrationBuilder.DropColumn(name: "AuthSubject", table: "Users");
        migrationBuilder.Sql("DROP INDEX [IX_Users_KeycloakSubject] ON [Users]; ALTER TABLE [Users] ALTER COLUMN [KeycloakSubject] nvarchar(200) NOT NULL; CREATE UNIQUE INDEX [IX_Users_KeycloakSubject] ON [Users] ([KeycloakSubject]);");
    }
}
