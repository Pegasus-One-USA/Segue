using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EntityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    NewValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthenticationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AuthenticationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataAccessLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PatientId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataAccessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Resolved = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CorrelationId",
                table: "AuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredOnUtc",
                table: "AuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_SequenceNumber",
                table: "AuditLogs",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_CorrelationId",
                table: "AuthenticationLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_OccurredOnUtc",
                table: "AuthenticationLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_UserEmail",
                table: "AuthenticationLogs",
                column: "UserEmail");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_CorrelationId",
                table: "DataAccessLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_OccurredOnUtc",
                table: "DataAccessLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_PatientId",
                table: "DataAccessLogs",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_PipelineRunId",
                table: "DataAccessLogs",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_OccurredOnUtc",
                table: "SecurityEvents",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Resolved",
                table: "SecurityEvents",
                column: "Resolved");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Severity",
                table: "SecurityEvents",
                column: "Severity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "AuthenticationLogs");

            migrationBuilder.DropTable(
                name: "DataAccessLogs");

            migrationBuilder.DropTable(
                name: "SecurityEvents");
        }
    }
}
