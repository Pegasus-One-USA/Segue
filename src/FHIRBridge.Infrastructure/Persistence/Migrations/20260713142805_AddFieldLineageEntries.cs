using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldLineageEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FieldLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SourceFieldPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TransformationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DestinationObject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldLineageEntries", x => x.Id);
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldLineageEntries");
        }
    }
}
