using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiRequestLogDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "ApiRequestLogs",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Outbound");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CorrelationId_Direction",
                table: "ApiRequestLogs",
                columns: new[] { "CorrelationId", "Direction" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_CorrelationId_Direction",
                table: "ApiRequestLogs");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "ApiRequestLogs");
        }
    }
}
