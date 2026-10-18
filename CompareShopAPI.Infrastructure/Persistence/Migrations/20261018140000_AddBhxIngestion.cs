using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261018140000_AddBhxIngestion")]
public sealed class AddBhxIngestion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE [DataSources] (
          [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DataSources] PRIMARY KEY,
          [Key] nvarchar(80) NOT NULL, [DisplayName] nvarchar(160) NOT NULL, [CategoryPath] nvarchar(80) NOT NULL,
          [PartnerShopId] int NULL, [State] nvarchar(20) NOT NULL, [IntervalHours] int NOT NULL,
          [CreatedAtUtc] datetime2 NOT NULL, [LastCompletedAtUtc] datetime2 NULL, [NextAttemptAtUtc] datetime2 NULL,
          CONSTRAINT [FK_DataSources_PartnerShops_PartnerShopId] FOREIGN KEY ([PartnerShopId]) REFERENCES [PartnerShops]([Id]) ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX [IX_DataSources_Key] ON [DataSources]([Key]);
        CREATE INDEX [IX_DataSources_PartnerShopId] ON [DataSources]([PartnerShopId]);
        CREATE TABLE [SyncJobs] (
          [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SyncJobs] PRIMARY KEY,
          [DataSourceId] int NOT NULL, [State] nvarchar(20) NOT NULL, [QueuedAtUtc] datetime2 NOT NULL,
          [StartedAtUtc] datetime2 NULL, [FinishedAtUtc] datetime2 NULL, [SeenCount] int NOT NULL,
          [ImportedCount] int NOT NULL, [PendingCount] int NOT NULL, [ErrorCode] nvarchar(50) NULL,
          [ErrorSummary] nvarchar(240) NULL, [RetryOfJobId] int NULL, [LeaseId] nvarchar(64) NOT NULL,
          CONSTRAINT [FK_SyncJobs_DataSources_DataSourceId] FOREIGN KEY ([DataSourceId]) REFERENCES [DataSources]([Id]) ON DELETE NO ACTION,
          CONSTRAINT [FK_SyncJobs_SyncJobs_RetryOfJobId] FOREIGN KEY ([RetryOfJobId]) REFERENCES [SyncJobs]([Id]) ON DELETE NO ACTION
        );
        CREATE INDEX [IX_SyncJobs_DataSourceId_State] ON [SyncJobs]([DataSourceId],[State]);
        CREATE UNIQUE INDEX [UX_SyncJobs_ActiveLease] ON [SyncJobs]([DataSourceId]) WHERE [State] = N'Running';
        CREATE UNIQUE INDEX [IX_SyncJobs_RetryOfJobId] ON [SyncJobs]([RetryOfJobId]) WHERE [RetryOfJobId] IS NOT NULL;
        CREATE TABLE [SourceItemMappings] (
          [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SourceItemMappings] PRIMARY KEY,
          [DataSourceId] int NOT NULL, [ExternalSku] nvarchar(160) NOT NULL, [Name] nvarchar(300) NOT NULL,
          [Brand] nvarchar(200) NULL, [ProductId] int NULL, [ProductOfferId] int NULL,
          CONSTRAINT [FK_SourceItemMappings_DataSources_DataSourceId] FOREIGN KEY ([DataSourceId]) REFERENCES [DataSources]([Id]) ON DELETE NO ACTION,
          CONSTRAINT [FK_SourceItemMappings_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products]([Id]) ON DELETE NO ACTION,
          CONSTRAINT [FK_SourceItemMappings_ProductOffers_ProductOfferId] FOREIGN KEY ([ProductOfferId]) REFERENCES [ProductOffers]([Id]) ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX [IX_SourceItemMappings_DataSourceId_ExternalSku] ON [SourceItemMappings]([DataSourceId],[ExternalSku]);
        CREATE INDEX [IX_SourceItemMappings_ProductId] ON [SourceItemMappings]([ProductId]);
        CREATE INDEX [IX_SourceItemMappings_ProductOfferId] ON [SourceItemMappings]([ProductOfferId]);
        ALTER TABLE [ProductMatchings] ADD [DataSourceId] int NULL, [ExternalSku] nvarchar(160) NULL;
        ALTER TABLE [ProductMatchings] ADD CONSTRAINT [FK_ProductMatchings_DataSources_DataSourceId] FOREIGN KEY ([DataSourceId]) REFERENCES [DataSources]([Id]) ON DELETE NO ACTION;
        CREATE UNIQUE INDEX [IX_ProductMatchings_DataSourceId_ExternalSku] ON [ProductMatchings]([DataSourceId],[ExternalSku]) WHERE [DataSourceId] IS NOT NULL AND [ExternalSku] IS NOT NULL;
        DECLARE @shopId int = (SELECT TOP 1 [Id] FROM [PartnerShops] WHERE [Slug] IN (N'bach-hoa-xanh', N'bachhoaxanh', N'bhx') ORDER BY CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END, [Id]);
        INSERT INTO [DataSources] ([Key],[DisplayName],[CategoryPath],[PartnerShopId],[State],[IntervalHours],[CreatedAtUtc],[LastCompletedAtUtc])
        VALUES (N'bhx',N'Bách Hóa Xanh',N'/bia',@shopId,N'Active',72,SYSUTCDATETIME(),NULL);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX [IX_ProductMatchings_DataSourceId_ExternalSku] ON [ProductMatchings];
        ALTER TABLE [ProductMatchings] DROP CONSTRAINT [FK_ProductMatchings_DataSources_DataSourceId];
        ALTER TABLE [ProductMatchings] DROP COLUMN [DataSourceId], [ExternalSku];
        DROP TABLE [SourceItemMappings]; DROP TABLE [SyncJobs]; DROP TABLE [DataSources];
        """);
}
