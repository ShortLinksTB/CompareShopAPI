using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261030140000_AddCellphoneSLaptopCategories")]
public sealed class AddCellphoneSLaptopCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF OBJECT_ID(N'[PartnerShops]', N'U') IS NULL OR OBJECT_ID(N'[DataSources]', N'U') IS NULL
          THROW 51000, 'CellphoneS category registration requires PartnerShops and DataSources.', 1;

        DECLARE @CellphoneSShopCount int = (SELECT COUNT(*) FROM [PartnerShops] WHERE [Slug] = N'cellphones');
        IF @CellphoneSShopCount <> 1
          THROW 51001, 'CellphoneS category registration requires exactly one existing CellphoneS PartnerShop.', 1;

        DECLARE @CellphoneSShopId int = (SELECT [Id] FROM [PartnerShops] WHERE [Slug] = N'cellphones');
        DECLARE @NewSources TABLE ([Key] nvarchar(80) NOT NULL, [DisplayName] nvarchar(200) NOT NULL, [CategoryPath] nvarchar(500) NOT NULL);
        INSERT INTO @NewSources ([Key], [DisplayName], [CategoryPath]) VALUES
          (N'cellphones-asus', N'CellphoneS Asus', N'/laptop/asus.html'),
          (N'cellphones-lenovo', N'CellphoneS Lenovo', N'/laptop/lenovo.html'),
          (N'cellphones-dell', N'CellphoneS Dell', N'/laptop/dell.html'),
          (N'cellphones-hp', N'CellphoneS HP', N'/laptop/hp.html'),
          (N'cellphones-acer', N'CellphoneS Acer', N'/laptop/acer.html');

        IF EXISTS (
          SELECT 1 FROM @NewSources expected
          JOIN [DataSources] existing ON existing.[Key] = expected.[Key]
          WHERE existing.[CategoryPath] <> expected.[CategoryPath] OR existing.[PartnerShopId] <> @CellphoneSShopId OR existing.[PartnerShopId] IS NULL)
          THROW 51002, 'Existing CellphoneS category source has a mismatched path or shop relation.', 1;

        INSERT INTO [DataSources] ([Key], [DisplayName], [CategoryPath], [PartnerShopId], [State], [IntervalHours], [CreatedAtUtc])
        SELECT expected.[Key], expected.[DisplayName], expected.[CategoryPath], @CellphoneSShopId, N'Active', 72, SYSUTCDATETIME()
        FROM @NewSources expected
        WHERE NOT EXISTS (SELECT 1 FROM [DataSources] existing WHERE existing.[Key] = expected.[Key]);
        """);

    // Registration is additive. Keep the rows on rollback so jobs and source-item relations are never orphaned.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
