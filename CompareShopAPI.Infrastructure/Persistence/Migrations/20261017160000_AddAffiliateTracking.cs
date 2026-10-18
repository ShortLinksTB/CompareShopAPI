using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261017160000_AddAffiliateTracking")]
public sealed class AddAffiliateTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("AffiliateLinks", table => new
        {
            Id = table.Column<int>(nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            ProductOfferId = table.Column<int>(nullable: false),
            DestinationUrl = table.Column<string>(maxLength: 2048, nullable: false),
            IsEnabled = table.Column<bool>(nullable: false),
            IsStale = table.Column<bool>(nullable: false),
            ClickCount = table.Column<int>(nullable: false),
            ConversionCount = table.Column<int>(nullable: false),
            Revenue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
            LastCheckedAtUtc = table.Column<DateTime>(nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AffiliateLinks", x => x.Id);
            table.ForeignKey("FK_AffiliateLinks_ProductOffers_ProductOfferId", x => x.ProductOfferId, "ProductOffers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable("AffiliateClicks", table => new
        {
            Id = table.Column<long>(nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            AffiliateLinkId = table.Column<int>(nullable: false),
            ClickedAtUtc = table.Column<DateTime>(nullable: false),
            ClientIpDigest = table.Column<string>(maxLength: 64, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AffiliateClicks", x => x.Id);
            table.ForeignKey("FK_AffiliateClicks_AffiliateLinks_AffiliateLinkId", x => x.AffiliateLinkId, "AffiliateLinks", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_AffiliateLinks_ProductOfferId", "AffiliateLinks", "ProductOfferId", unique: true);
        migrationBuilder.CreateIndex("IX_AffiliateClicks_AffiliateLinkId_ClickedAtUtc", "AffiliateClicks", new[] { "AffiliateLinkId", "ClickedAtUtc" });
        migrationBuilder.Sql("""
            INSERT INTO [AffiliateLinks] ([ProductOfferId], [DestinationUrl], [IsEnabled], [IsStale], [ClickCount], [ConversionCount], [Revenue], [LastCheckedAtUtc])
            SELECT [Id],
                CASE WHEN [Url] LIKE N'https://%'
                    THEN [Url] ELSE CONCAT(N'https://example.invalid/offer/', CONVERT(varchar(20), [Id])) END,
                CASE WHEN [Url] LIKE N'https://%' THEN 1 ELSE 0 END,
                0, 0, 0, 0, NULL
            FROM [ProductOffers];
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AffiliateClicks");
        migrationBuilder.DropTable("AffiliateLinks");
    }
}
