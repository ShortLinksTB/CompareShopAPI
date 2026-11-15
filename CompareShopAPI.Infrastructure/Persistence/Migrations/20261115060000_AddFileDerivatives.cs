using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261115060000_AddFileDerivatives")]
public sealed class AddFileDerivatives : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "DerivedFromFileId", table: "Files", type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<string>(name: "DerivedPreset", table: "Files", type: "nvarchar(40)", maxLength: 40, nullable: true);
        migrationBuilder.AddColumn<string>(name: "DerivativeSpecJson", table: "Files", type: "nvarchar(max)", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Files_DerivedFromFileId", table: "Files", column: "DerivedFromFileId");
        migrationBuilder.AddForeignKey(name: "FK_Files_Files_DerivedFromFileId", table: "Files", column: "DerivedFromFileId",
            principalTable: "Files", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Files_Files_DerivedFromFileId", table: "Files");
        migrationBuilder.DropIndex(name: "IX_Files_DerivedFromFileId", table: "Files");
        migrationBuilder.DropColumn(name: "DerivedFromFileId", table: "Files");
        migrationBuilder.DropColumn(name: "DerivedPreset", table: "Files");
        migrationBuilder.DropColumn(name: "DerivativeSpecJson", table: "Files");
    }
}
