using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorReportCorrelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorReportCorrelations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    RunStatus = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RunStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RunCompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtractedCount = table.Column<int>(type: "integer", nullable: true),
                    MappedCount = table.Column<int>(type: "integer", nullable: true),
                    WrittenCount = table.Column<int>(type: "integer", nullable: true),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    EventsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorReportCorrelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErrorReportCorrelations_ErrorReportImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "ErrorReportImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReportCorrelations_ImportId_CorrelationId",
                table: "ErrorReportCorrelations",
                columns: new[] { "ImportId", "CorrelationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorReportCorrelations");
        }
    }
}
