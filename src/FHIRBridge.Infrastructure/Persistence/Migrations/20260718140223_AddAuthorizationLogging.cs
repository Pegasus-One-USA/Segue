using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorizationLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthorizationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_CorrelationId",
                table: "AuthorizationLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_OccurredOnUtc",
                table: "AuthorizationLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_UserEmail",
                table: "AuthorizationLogs",
                column: "UserEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorizationLogs");
        }
    }
}
