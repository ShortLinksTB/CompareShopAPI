using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompareShopAPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignLegacyCatalogSeedRowsToVN : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @vnCountryId int;
                SELECT @vnCountryId = [Id] FROM [Countries] WHERE [Code] = N'VN';
                IF @vnCountryId IS NULL
                    THROW 51010, 'Cannot assign approved legacy catalog seeds: Country VN is missing. Ensure countries.initial.json has been seeded.', 1;

                IF (SELECT COUNT_BIG(*) FROM [PartnerShops] WHERE [Slug] = N'cellphones') > 1
                    THROW 51011, 'Cannot assign approved legacy catalog seed: PartnerShop slug cellphones is duplicated.', 1;
                IF (SELECT COUNT_BIG(*) FROM [Categories] WHERE [Slug] = N'electronics') > 1
                    THROW 51012, 'Cannot assign approved legacy catalog seed: Category slug electronics is duplicated.', 1;

                IF EXISTS (SELECT 1 FROM [PartnerShops] WHERE [Slug] = N'cellphones' AND [CountryId] IS NOT NULL AND [CountryId] <> @vnCountryId)
                    THROW 51013, 'Cannot assign approved legacy catalog seed: PartnerShop cellphones already belongs to another Country.', 1;
                IF EXISTS (SELECT 1 FROM [Categories] WHERE [Slug] = N'electronics' AND [CountryId] IS NOT NULL AND [CountryId] <> @vnCountryId)
                    THROW 51014, 'Cannot assign approved legacy catalog seed: Category electronics already belongs to another Country.', 1;

                UPDATE [PartnerShops] SET [CountryId] = @vnCountryId
                WHERE [Slug] = N'cellphones' AND [CountryId] IS NULL;
                UPDATE [Categories] SET [CountryId] = @vnCountryId
                WHERE [Slug] = N'electronics' AND [CountryId] IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This migration only assigns approved seed rows. Reverting those assignments
            // would erase legitimate Country ownership, so intentionally do not undo data.
        }
    }
}
