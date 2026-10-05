using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorReportImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorReportImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ReportFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReportToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReportGeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApplicationVersion = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ReportTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorReportImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorReportEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErrorReferenceId = table.Column<string>(type: "text", nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: true),
                    Module = table.Column<string>(type: "text", nullable: true),
                    ExceptionType = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    WhatToDo = table.Column<string>(type: "text", nullable: true),
                    Cause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    ExecutionId = table.Column<string>(type: "text", nullable: true),
                    WorkflowId = table.Column<string>(type: "text", nullable: true),
                    EndpointId = table.Column<string>(type: "text", nullable: true),
                    TraceId = table.Column<string>(type: "text", nullable: true),
                    StackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorReportEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErrorReportEntries_ErrorReportImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "ErrorReportImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReportEntries_ImportId_OccurredOnUtc",
                table: "ErrorReportEntries",
                columns: new[] { "ImportId", "OccurredOnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReportImports_ClientName",
                table: "ErrorReportImports",
                column: "ClientName");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReportImports_ImportedAtUtc",
                table: "ErrorReportImports",
                column: "ImportedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorReportEntries");

            migrationBuilder.DropTable(
                name: "ErrorReportImports");
        }
    }
}
