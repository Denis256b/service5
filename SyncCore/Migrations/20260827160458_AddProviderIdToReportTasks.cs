using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCore.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderIdToReportTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "ReportTasks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "ReportTasks");
        }
    }
}
