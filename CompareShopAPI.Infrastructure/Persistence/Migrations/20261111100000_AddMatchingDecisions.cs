using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261111100000_AddMatchingDecisions")]
public sealed class AddMatchingDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Status", "ProductMatchings", type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "pending");
        migrationBuilder.AddColumn<string>("SkipReason", "ProductMatchings", type: "nvarchar(80)", maxLength: 80, nullable: true);
        migrationBuilder.AddColumn<string>("SkipNote", "ProductMatchings", type: "nvarchar(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>("DecisionActor", "ProductMatchings", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<DateTime>("DecisionAtUtc", "ProductMatchings", type: "datetime2", nullable: true);
        migrationBuilder.CreateTable("MatchingDecisions", table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            MatchingId = table.Column<int>(type: "int", nullable: false),
            Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
            Reason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
            Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
            ActorSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_MatchingDecisions", x => x.Id));
        migrationBuilder.CreateIndex("IX_MatchingDecisions_MatchingId_CreatedAtUtc", "MatchingDecisions", new[] { "MatchingId", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("MatchingDecisions");
        migrationBuilder.DropColumn("Status", "ProductMatchings");
        migrationBuilder.DropColumn("SkipReason", "ProductMatchings");
        migrationBuilder.DropColumn("SkipNote", "ProductMatchings");
        migrationBuilder.DropColumn("DecisionActor", "ProductMatchings");
        migrationBuilder.DropColumn("DecisionAtUtc", "ProductMatchings");
    }
}
