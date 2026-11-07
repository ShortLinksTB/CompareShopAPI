using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261107140000_AddCategoryHierarchy")]
public sealed class AddCategoryHierarchy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ParentCategoryId",
            table: "Categories",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<byte>(
            name: "Level",
            table: "Categories",
            type: "tinyint",
            nullable: false,
            defaultValue: (byte)1);

        migrationBuilder.AddColumn<int>(
            name: "SortOrder",
            table: "Categories",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql("""
            IF NOT EXISTS (SELECT 1 FROM [Categories] WHERE [Slug] = N'electronics')
                INSERT INTO [Categories] ([Slug], [Name], [IsActive], [Level], [SortOrder])
                VALUES (N'electronics', N'Electronics', 1, 1, 0);

            UPDATE [parent] SET [SortOrder] = 0
            FROM [Categories] AS [parent]
            WHERE [parent].[Slug] = N'electronics';

            UPDATE child
            SET [child].[ParentCategoryId] = [parent].[Id],
                [child].[Level] = 2,
                [child].[SortOrder] = CASE [child].[Slug]
                    WHEN N'phones' THEN 0
                    WHEN N'computers' THEN 1
                    WHEN N'audio' THEN 2
                END
            FROM [Categories] AS [child]
            CROSS JOIN [Categories] AS [parent]
            WHERE [parent].[Slug] = N'electronics'
              AND [child].[Slug] IN (N'phones', N'computers', N'audio');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Categories_ParentCategoryId",
            table: "Categories",
            column: "ParentCategoryId");

        migrationBuilder.AddForeignKey(
            name: "FK_Categories_Categories_ParentCategoryId",
            table: "Categories",
            column: "ParentCategoryId",
            principalTable: "Categories",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Categories_Level_ParentCategoryId",
            table: "Categories",
            sql: "([Level] = 1 AND [ParentCategoryId] IS NULL) OR ([Level] = 2 AND [ParentCategoryId] IS NOT NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Categories_Level_ParentCategoryId",
            table: "Categories");

        migrationBuilder.DropForeignKey(
            name: "FK_Categories_Categories_ParentCategoryId",
            table: "Categories");

        migrationBuilder.DropIndex(
            name: "IX_Categories_ParentCategoryId",
            table: "Categories");

        migrationBuilder.DropColumn(name: "ParentCategoryId", table: "Categories");
        migrationBuilder.DropColumn(name: "Level", table: "Categories");
        migrationBuilder.DropColumn(name: "SortOrder", table: "Categories");
    }
}
