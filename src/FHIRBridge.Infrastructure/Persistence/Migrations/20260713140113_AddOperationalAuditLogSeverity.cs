using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAuditLogSeverity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "OperationalAuditLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Information");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_Severity",
                table: "OperationalAuditLogs",
                column: "Severity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationalAuditLogs_Severity",
                table: "OperationalAuditLogs");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "OperationalAuditLogs");
        }
    }
}
