using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CompareShopAPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompareShopDbContext))]
[Migration("20261115050000_AddSyncJobLogEntries")]
public sealed class AddSyncJobLogEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SyncJobLogEntries",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                JobId = table.Column<int>(type: "int", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                Level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Step = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                DurationMs = table.Column<int>(type: "int", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SyncJobLogEntries", x => x.Id);
                table.ForeignKey("FK_SyncJobLogEntries_SyncJobs_JobId", x => x.JobId, "SyncJobs", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_SyncJobLogEntries_JobId_OccurredAtUtc_Id", "SyncJobLogEntries", new[] { "JobId", "OccurredAtUtc", "Id" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "SyncJobLogEntries");
}
