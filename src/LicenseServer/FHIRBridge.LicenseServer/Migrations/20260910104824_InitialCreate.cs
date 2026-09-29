using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssuedLicenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<string>(type: "text", nullable: false),
                    CustomerName = table.Column<string>(type: "text", nullable: false),
                    Edition = table.Column<string>(type: "text", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxUsers = table.Column<int>(type: "integer", nullable: false),
                    MaxWorkflows = table.Column<int>(type: "integer", nullable: false),
                    MaxSourceConnections = table.Column<int>(type: "integer", nullable: false),
                    MaxProcessedRecordsPerMonth = table.Column<int>(type: "integer", nullable: false),
                    AllowedSourceTypesSummary = table.Column<string>(type: "text", nullable: true),
                    FeaturesSummary = table.Column<string>(type: "text", nullable: true),
                    AllowedResourceTypesSummary = table.Column<string>(type: "text", nullable: true),
                    AllowedDestinationTypesSummary = table.Column<string>(type: "text", nullable: true),
                    ClaimsJson = table.Column<string>(type: "text", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuedLicenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CheckIns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstallationId = table.Column<string>(type: "text", nullable: false),
                    CurrentIssuedLicenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserCount = table.Column<int>(type: "integer", nullable: false),
                    SourceConnectionCount = table.Column<int>(type: "integer", nullable: false),
                    TenantCount = table.Column<int>(type: "integer", nullable: false),
                    WorkflowCount = table.Column<int>(type: "integer", nullable: false),
                    CumulativeConfiguredPipelineRunCount = table.Column<long>(type: "bigint", nullable: false),
                    CumulativeRuntimeWorkflowRunCount = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedRecordsThisMonth = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckIns_IssuedLicenses_CurrentIssuedLicenseId",
                        column: x => x.CurrentIssuedLicenseId,
                        principalTable: "IssuedLicenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "text", nullable: false),
                    CustomerId = table.Column<string>(type: "text", nullable: true),
                    CustomerName = table.Column<string>(type: "text", nullable: true),
                    Edition = table.Column<string>(type: "text", nullable: true),
                    CurrentIssuedLicenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUserCount = table.Column<int>(type: "integer", nullable: false),
                    LastSourceConnectionCount = table.Column<int>(type: "integer", nullable: false),
                    LastTenantCount = table.Column<int>(type: "integer", nullable: false),
                    LastWorkflowCount = table.Column<int>(type: "integer", nullable: false),
                    LastCumulativeConfiguredPipelineRunCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCumulativeRuntimeWorkflowRunCount = table.Column<long>(type: "bigint", nullable: false),
                    LastProcessedRecordsThisMonth = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.InstallationId);
                    table.ForeignKey(
                        name: "FK_Installations_IssuedLicenses_CurrentIssuedLicenseId",
                        column: x => x.CurrentIssuedLicenseId,
                        principalTable: "IssuedLicenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_CurrentIssuedLicenseId",
                table: "CheckIns",
                column: "CurrentIssuedLicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_InstallationId_ReceivedAtUtc",
                table: "CheckIns",
                columns: new[] { "InstallationId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CurrentIssuedLicenseId",
                table: "Installations",
                column: "CurrentIssuedLicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_LastSeenUtc",
                table: "Installations",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IssuedLicenses_IssuedAtUtc",
                table: "IssuedLicenses",
                column: "IssuedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckIns");

            migrationBuilder.DropTable(
                name: "Installations");

            migrationBuilder.DropTable(
                name: "IssuedLicenses");
        }
    }
}
