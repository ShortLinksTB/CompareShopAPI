using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261115070000_AddHomepagePartnerVisibility")]
public sealed class AddHomepagePartnerVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ShowOnHome", table: "PartnerShops", type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>(
            name: "HomeDisplayOrder", table: "PartnerShops", type: "int", nullable: true);

        migrationBuilder.Sql("""
            SET NOCOUNT ON;
            DECLARE @Targets TABLE ([DisplayOrder] int NOT NULL, [Key] nvarchar(100) NOT NULL);
            INSERT INTO @Targets ([DisplayOrder], [Key]) VALUES
                (1, N'shopee'), (2, N'lazada'), (3, N'tiki'), (4, N'fpt'),
                (5, N'cellphones'), (6, N'viettel'), (7, N'hoanghamobile'),
                (8, N'thegioididong'), (9, N'dienmayxanh'), (10, N'nguyenkim'),
                (11, N'pico'), (12, N'senheng');

            DECLARE @Matched TABLE ([DisplayOrder] int NOT NULL PRIMARY KEY, [ShopId] int NOT NULL UNIQUE);
            DECLARE @Order int, @Key nvarchar(100), @ShopId int;
            DECLARE target_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT [DisplayOrder], [Key] FROM @Targets ORDER BY [DisplayOrder];
            OPEN target_cursor;
            FETCH NEXT FROM target_cursor INTO @Order, @Key;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @ShopId = NULL;
                SELECT TOP (1) @ShopId = shop.[Id]
                FROM [PartnerShops] AS shop
                WHERE shop.[IsActive] = 1
                  AND NOT EXISTS (SELECT 1 FROM @Matched matched WHERE matched.[ShopId] = shop.[Id])
                  AND (
                    REPLACE(TRANSLATE(LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(shop.[Name], CHAR(9), N' '), CHAR(10), N' '), CHAR(13), N' '), NCHAR(160), N' '), N'’', N' ')), N' !"#$%&''()*+,-./:;<=>?@[\]^_`{|}~', REPLICATE(N' ', 33)), N' ', N'') COLLATE Latin1_General_100_CI_AI = @Key
                    OR REPLACE(TRANSLATE(LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(shop.[Slug], CHAR(9), N' '), CHAR(10), N' '), CHAR(13), N' '), NCHAR(160), N' '), N'’', N' ')), N' !"#$%&''()*+,-./:;<=>?@[\]^_`{|}~', REPLICATE(N' ', 33)), N' ', N'') COLLATE Latin1_General_100_CI_AI = @Key
                  )
                ORDER BY CASE WHEN LOWER(shop.[Slug]) COLLATE Latin1_General_100_CI_AI = @Key THEN 0 ELSE 1 END, shop.[Id];

                IF @ShopId IS NOT NULL
                BEGIN
                    UPDATE [PartnerShops]
                    SET [ShowOnHome] = 1, [HomeDisplayOrder] = @Order
                    WHERE [Id] = @ShopId AND [IsActive] = 1;
                    INSERT INTO @Matched ([DisplayOrder], [ShopId]) VALUES (@Order, @ShopId);
                END;
                FETCH NEXT FROM target_cursor INTO @Order, @Key;
            END;
            CLOSE target_cursor;
            DEALLOCATE target_cursor;

            DECLARE @MatchedCount int = (SELECT COUNT(*) FROM @Matched);
            DECLARE @UnmatchedCount int = 12 - @MatchedCount;
            DECLARE @Message nvarchar(200) = CONCAT(N'Homepage partner backfill: matched ', @MatchedCount, N' active shops; unmatched ', @UnmatchedCount, N' names.');
            RAISERROR(N'%s', 10, 1, @Message) WITH NOWAIT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HomeDisplayOrder", table: "PartnerShops");
        migrationBuilder.DropColumn(name: "ShowOnHome", table: "PartnerShops");
    }
}
