using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserActivityAuditLogModuleActionOldNewValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "UserActivityAuditLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Module",
                table: "UserActivityAuditLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NewValue",
                table: "UserActivityAuditLogs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OldValue",
                table: "UserActivityAuditLogs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_Module",
                table: "UserActivityAuditLogs",
                column: "Module");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserActivityAuditLogs_Module",
                table: "UserActivityAuditLogs");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "UserActivityAuditLogs");

            migrationBuilder.DropColumn(
                name: "Module",
                table: "UserActivityAuditLogs");

            migrationBuilder.DropColumn(
                name: "NewValue",
                table: "UserActivityAuditLogs");

            migrationBuilder.DropColumn(
                name: "OldValue",
                table: "UserActivityAuditLogs");
        }
    }
}
