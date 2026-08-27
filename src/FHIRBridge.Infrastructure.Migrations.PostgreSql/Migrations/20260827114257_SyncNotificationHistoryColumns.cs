using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SyncNotificationHistoryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentNames",
                table: "NotificationHistory",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "NotificationHistory",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentNames",
                table: "NotificationHistory");

            migrationBuilder.DropColumn(
                name: "Body",
                table: "NotificationHistory");
        }
    }
}
