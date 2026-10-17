using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261017150000_AddPriceHistoryPoints")]
public sealed class AddPriceHistoryPoints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
            THROW 51001, 'PriceHistoryPoints migration requires dbo.Products; no objects were changed.', 1;
        IF OBJECT_ID(N'dbo.PartnerShops', N'U') IS NULL
            THROW 51002, 'PriceHistoryPoints migration requires dbo.PartnerShops; no objects were changed.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = N'dbo' AND t.name = N'Products' AND c.name = N'Id' AND ty.name = N'int' AND c.is_identity = 1
              AND EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                          WHERE i.object_id = t.object_id AND i.is_primary_key = 1 AND i.is_disabled = 0 AND ic.column_id = c.column_id AND ic.key_ordinal = 1
                            AND NOT EXISTS (SELECT 1 FROM sys.index_columns extra WHERE extra.object_id = i.object_id AND extra.index_id = i.index_id AND extra.key_ordinal > 1)))
            THROW 51003, 'PriceHistoryPoints migration requires dbo.Products.Id to be an int identity primary key; no objects were changed.', 1;
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = N'dbo' AND t.name = N'PartnerShops' AND c.name = N'Id' AND ty.name = N'int' AND c.is_identity = 1
              AND EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                          WHERE i.object_id = t.object_id AND i.is_primary_key = 1 AND i.is_disabled = 0 AND ic.column_id = c.column_id AND ic.key_ordinal = 1
                            AND NOT EXISTS (SELECT 1 FROM sys.index_columns extra WHERE extra.object_id = i.object_id AND extra.index_id = i.index_id AND extra.key_ordinal > 1)))
            THROW 51004, 'PriceHistoryPoints migration requires dbo.PartnerShops.Id to be an int identity primary key; no objects were changed.', 1;

        IF OBJECT_ID(N'dbo.PriceHistoryPoints', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.PriceHistoryPoints
            (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PriceHistoryPoints PRIMARY KEY,
                ProductId int NOT NULL,
                DateUtc datetime2 NOT NULL,
                MinPriceVnd int NOT NULL,
                PartnerShopId int NULL
            );
        END
        ELSE IF
            (SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
             WHERE c.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND
               ((c.name = N'Id' AND ty.name = N'int' AND c.is_nullable = 0 AND c.is_identity = 1) OR
                (c.name = N'ProductId' AND ty.name = N'int' AND c.is_nullable = 0 AND c.is_identity = 0) OR
                (c.name = N'DateUtc' AND ty.name = N'datetime2' AND c.is_nullable = 0 AND c.is_identity = 0) OR
                (c.name = N'MinPriceVnd' AND ty.name = N'int' AND c.is_nullable = 0 AND c.is_identity = 0) OR
                (c.name = N'PartnerShopId' AND ty.name = N'int' AND c.is_nullable = 1 AND c.is_identity = 0))) <> 5
            OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PriceHistoryPoints')) <> 5
            THROW 51005, 'Existing dbo.PriceHistoryPoints is partial or incompatible; inspect it before retrying this migration.', 1;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE i.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND i.is_primary_key = 1 AND i.is_disabled = 0 AND c.name = N'Id' AND ic.key_ordinal = 1
                         AND NOT EXISTS (SELECT 1 FROM sys.index_columns extra WHERE extra.object_id = i.object_id AND extra.index_id = i.index_id AND extra.key_ordinal > 1))
            THROW 51006, 'Existing dbo.PriceHistoryPoints has no compatible Id primary key; inspect it before retrying this migration.', 1;

        IF EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                   WHERE fkc.parent_object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'ProductId'
                     AND NOT (OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'dbo' AND OBJECT_NAME(fk.referenced_object_id) = N'Products'
                       AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id' AND fk.delete_referential_action = 0 AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
                       AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns extra WHERE extra.constraint_object_id = fk.object_id AND extra.constraint_column_id <> fkc.constraint_column_id)))
            THROW 51007, 'Existing ProductId foreign key on dbo.PriceHistoryPoints is incompatible.', 1;
        IF NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                       WHERE fkc.parent_object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'ProductId'
                         AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'dbo' AND OBJECT_NAME(fk.referenced_object_id) = N'Products'
                         AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id' AND fk.delete_referential_action = 0 AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
                         AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns extra WHERE extra.constraint_object_id = fk.object_id AND extra.constraint_column_id <> fkc.constraint_column_id))
            ALTER TABLE dbo.PriceHistoryPoints ADD CONSTRAINT FK_PriceHistoryPoints_Products_ProductId FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id) ON DELETE NO ACTION;

        IF EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                   WHERE fkc.parent_object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'PartnerShopId'
                     AND NOT (OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'dbo' AND OBJECT_NAME(fk.referenced_object_id) = N'PartnerShops'
                       AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id' AND fk.delete_referential_action = 2 AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
                       AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns extra WHERE extra.constraint_object_id = fk.object_id AND extra.constraint_column_id <> fkc.constraint_column_id)))
            THROW 51008, 'Existing PartnerShopId foreign key on dbo.PriceHistoryPoints is incompatible.', 1;
        IF NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                       WHERE fkc.parent_object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'PartnerShopId'
                         AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'dbo' AND OBJECT_NAME(fk.referenced_object_id) = N'PartnerShops'
                         AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id' AND fk.delete_referential_action = 2 AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
                         AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns extra WHERE extra.constraint_object_id = fk.object_id AND extra.constraint_column_id <> fkc.constraint_column_id))
            ALTER TABLE dbo.PriceHistoryPoints ADD CONSTRAINT FK_PriceHistoryPoints_PartnerShops_PartnerShopId FOREIGN KEY (PartnerShopId) REFERENCES dbo.PartnerShops(Id) ON DELETE SET NULL;

        IF EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND i.name = N'IX_PriceHistoryPoints_ProductId_DateUtc'
                   AND (i.is_disabled = 1 OR (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0) <> 2
                     OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'ProductId')
                     OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'DateUtc')))
            THROW 51009, 'Existing price history index has incompatible columns.', 1;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND i.name = N'IX_PriceHistoryPoints_ProductId_DateUtc')
            CREATE INDEX IX_PriceHistoryPoints_ProductId_DateUtc ON dbo.PriceHistoryPoints(ProductId, DateUtc);

        IF EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND i.name = N'IX_PriceHistoryPoints_PartnerShopId'
                   AND (i.is_disabled = 1 OR (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0) <> 1
                     OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'PartnerShopId')))
            THROW 51010, 'Existing partner shop history index has incompatible columns.', 1;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.PriceHistoryPoints') AND i.name = N'IX_PriceHistoryPoints_PartnerShopId')
            CREATE INDEX IX_PriceHistoryPoints_PartnerShopId ON dbo.PriceHistoryPoints(PartnerShopId);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("IF OBJECT_ID(N'dbo.PriceHistoryPoints', N'U') IS NOT NULL DROP TABLE dbo.PriceHistoryPoints;");
}
