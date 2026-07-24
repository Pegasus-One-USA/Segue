using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGovernanceAuditLoggingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldLineageEntries");

            migrationBuilder.DropTable(
                name: "OperationalAuditLogs");

            migrationBuilder.DropTable(
                name: "ResourceLineageEntries");

            migrationBuilder.DropTable(
                name: "UserActivityAuditLogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FieldLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DestinationObject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceFieldPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TransformationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldLineageEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceCount = table.Column<int>(type: "int", nullable: true),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Information"),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceLineageEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserActivityAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Activity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivityAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_OccurredOnUtc",
                table: "FieldLineageEntries",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_PipelineRunId",
                table: "FieldLineageEntries",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_SourceResourceId",
                table: "FieldLineageEntries",
                column: "SourceResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_OccurredOnUtc",
                table: "OperationalAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_Severity",
                table: "OperationalAuditLogs",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_OccurredOnUtc",
                table: "ResourceLineageEntries",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_PipelineRunId",
                table: "ResourceLineageEntries",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_SourceResourceId",
                table: "ResourceLineageEntries",
                column: "SourceResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_Module",
                table: "UserActivityAuditLogs",
                column: "Module");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_OccurredOnUtc",
                table: "UserActivityAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_UserId",
                table: "UserActivityAuditLogs",
                column: "UserId");
        }
    }
}
