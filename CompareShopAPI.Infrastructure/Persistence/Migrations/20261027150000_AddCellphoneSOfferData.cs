using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261027150000_AddCellphoneSOfferData")]
public sealed class AddCellphoneSOfferData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF OBJECT_ID(N'[ProductOffers]', N'U') IS NOT NULL
        BEGIN
          IF COL_LENGTH(N'dbo.ProductOffers', N'ImageUrl') IS NULL ALTER TABLE [ProductOffers] ADD [ImageUrl] nvarchar(2048) NULL;
          IF COL_LENGTH(N'dbo.ProductOffers', N'OriginalPriceVnd') IS NULL ALTER TABLE [ProductOffers] ADD [OriginalPriceVnd] int NULL;
          IF COL_LENGTH(N'dbo.ProductOffers', N'SpecificationsJson') IS NULL ALTER TABLE [ProductOffers] ADD [SpecificationsJson] nvarchar(max) NULL;
          IF COL_LENGTH(N'dbo.ProductOffers', N'StockStatus') IS NULL ALTER TABLE [ProductOffers] ADD [StockStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_ProductOffers_StockStatus] DEFAULT N'unknown';
        END;
        IF OBJECT_ID(N'[SourceItemMappings]', N'U') IS NOT NULL
        BEGIN
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'ImageUrl') IS NULL ALTER TABLE [SourceItemMappings] ADD [ImageUrl] nvarchar(2048) NULL;
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'OriginalPriceVnd') IS NULL ALTER TABLE [SourceItemMappings] ADD [OriginalPriceVnd] int NULL;
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'SpecificationsJson') IS NULL ALTER TABLE [SourceItemMappings] ADD [SpecificationsJson] nvarchar(max) NULL;
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'StockStatus') IS NULL ALTER TABLE [SourceItemMappings] ADD [StockStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_SourceItemMappings_StockStatus] DEFAULT N'unknown';
        END;
        IF OBJECT_ID(N'[ProductOfferSnapshots]', N'U') IS NULL AND OBJECT_ID(N'[ProductOffers]', N'U') IS NOT NULL
        BEGIN
          CREATE TABLE [ProductOfferSnapshots] (
            [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ProductOfferSnapshots] PRIMARY KEY,
            [ProductOfferId] int NOT NULL, [PriceVnd] int NOT NULL, [Url] nvarchar(2048) NULL, [ImageUrl] nvarchar(2048) NULL,
            [OriginalPriceVnd] int NULL, [SpecificationsJson] nvarchar(max) NULL,
            [StockStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_ProductOfferSnapshots_StockStatus] DEFAULT N'unknown',
            [CapturedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [FK_ProductOfferSnapshots_ProductOffers_ProductOfferId] FOREIGN KEY ([ProductOfferId]) REFERENCES [ProductOffers]([Id]) ON DELETE CASCADE
          );
          CREATE INDEX [IX_ProductOfferSnapshots_ProductOfferId_CapturedAtUtc] ON [ProductOfferSnapshots]([ProductOfferId], [CapturedAtUtc]);
        END;
        IF OBJECT_ID(N'[ProductOfferSnapshots]', N'U') IS NOT NULL AND OBJECT_ID(N'[ProductOffers]', N'U') IS NOT NULL
        BEGIN
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'ProductOfferId') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [ProductOfferId] int NULL;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'PriceVnd') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [PriceVnd] int NOT NULL CONSTRAINT [DF_ProductOfferSnapshots_PriceVnd] DEFAULT 0;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'Url') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [Url] nvarchar(2048) NULL;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'ImageUrl') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [ImageUrl] nvarchar(2048) NULL;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'OriginalPriceVnd') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [OriginalPriceVnd] int NULL;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'SpecificationsJson') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [SpecificationsJson] nvarchar(max) NULL;
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'StockStatus') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [StockStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_ProductOfferSnapshots_StockStatus] DEFAULT N'unknown';
          IF COL_LENGTH(N'dbo.ProductOfferSnapshots', N'CapturedAtUtc') IS NULL ALTER TABLE [ProductOfferSnapshots] ADD [CapturedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_ProductOfferSnapshots_CapturedAtUtc] DEFAULT SYSUTCDATETIME();
          IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_ProductOfferSnapshots_ProductOffers_ProductOfferId')
             AND NOT EXISTS (SELECT 1 FROM [ProductOfferSnapshots] AS snapshot WHERE snapshot.[ProductOfferId] IS NULL OR NOT EXISTS (SELECT 1 FROM [ProductOffers] AS offer WHERE offer.[Id] = snapshot.[ProductOfferId]))
            ALTER TABLE [ProductOfferSnapshots] ADD CONSTRAINT [FK_ProductOfferSnapshots_ProductOffers_ProductOfferId] FOREIGN KEY ([ProductOfferId]) REFERENCES [ProductOffers]([Id]) ON DELETE CASCADE;
          IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ProductOfferSnapshots_ProductOfferId_CapturedAtUtc' AND [object_id] = OBJECT_ID(N'dbo.ProductOfferSnapshots'))
            CREATE INDEX [IX_ProductOfferSnapshots_ProductOfferId_CapturedAtUtc] ON [ProductOfferSnapshots]([ProductOfferId], [CapturedAtUtc]);
        END;
        IF OBJECT_ID(N'[PartnerShops]', N'U') IS NOT NULL AND OBJECT_ID(N'[DataSources]', N'U') IS NOT NULL
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM [PartnerShops] WHERE [Slug] = N'cellphones')
            INSERT INTO [PartnerShops] ([Slug],[Name],[WebsiteUrl],[IsActive]) VALUES (N'cellphones',N'CellphoneS',N'https://cellphones.com.vn',1);
          IF EXISTS (SELECT 1 FROM [PartnerShops] WHERE [Slug] = N'cellphones')
          BEGIN
            DECLARE @CellphoneSShopId int = (SELECT TOP (1) [Id] FROM [PartnerShops] WHERE [Slug] = N'cellphones' ORDER BY [Id]);
            IF NOT EXISTS (SELECT 1 FROM [DataSources] WHERE [Key] = N'cellphones')
              INSERT INTO [DataSources] ([Key],[DisplayName],[CategoryPath],[PartnerShopId],[State],[IntervalHours],[CreatedAtUtc]) VALUES (N'cellphones',N'CellphoneS',N'/laptop/mac.html',@CellphoneSShopId,N'Active',72,SYSUTCDATETIME());
            ELSE
              UPDATE [DataSources] SET [PartnerShopId] = @CellphoneSShopId, [CategoryPath] = N'/laptop/mac.html' WHERE [Key] = N'cellphones' AND ([PartnerShopId] IS NULL OR [CategoryPath] <> N'/laptop/mac.html');
          END;
        END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF OBJECT_ID(N'[ProductOfferSnapshots]', N'U') IS NOT NULL DROP TABLE [ProductOfferSnapshots];
        IF OBJECT_ID(N'[ProductOffers]', N'U') IS NOT NULL
        BEGIN
          IF OBJECT_ID(N'DF_ProductOffers_StockStatus', N'D') IS NOT NULL ALTER TABLE [ProductOffers] DROP CONSTRAINT [DF_ProductOffers_StockStatus];
          IF COL_LENGTH(N'dbo.ProductOffers', N'StockStatus') IS NOT NULL ALTER TABLE [ProductOffers] DROP COLUMN [StockStatus];
          IF COL_LENGTH(N'dbo.ProductOffers', N'ImageUrl') IS NOT NULL ALTER TABLE [ProductOffers] DROP COLUMN [ImageUrl];
          IF COL_LENGTH(N'dbo.ProductOffers', N'OriginalPriceVnd') IS NOT NULL ALTER TABLE [ProductOffers] DROP COLUMN [OriginalPriceVnd];
          IF COL_LENGTH(N'dbo.ProductOffers', N'SpecificationsJson') IS NOT NULL ALTER TABLE [ProductOffers] DROP COLUMN [SpecificationsJson];
        END;
        IF OBJECT_ID(N'[SourceItemMappings]', N'U') IS NOT NULL
        BEGIN
          IF OBJECT_ID(N'DF_SourceItemMappings_StockStatus', N'D') IS NOT NULL ALTER TABLE [SourceItemMappings] DROP CONSTRAINT [DF_SourceItemMappings_StockStatus];
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'StockStatus') IS NOT NULL ALTER TABLE [SourceItemMappings] DROP COLUMN [StockStatus];
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'ImageUrl') IS NOT NULL ALTER TABLE [SourceItemMappings] DROP COLUMN [ImageUrl];
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'OriginalPriceVnd') IS NOT NULL ALTER TABLE [SourceItemMappings] DROP COLUMN [OriginalPriceVnd];
          IF COL_LENGTH(N'dbo.SourceItemMappings', N'SpecificationsJson') IS NOT NULL ALTER TABLE [SourceItemMappings] DROP COLUMN [SpecificationsJson];
        END;
        """);
}
