using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfiguredPipelineRunCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ConfiguredPipelineRuns",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_CorrelationId",
                table: "ConfiguredPipelineRuns",
                column: "CorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConfiguredPipelineRuns_CorrelationId",
                table: "ConfiguredPipelineRuns");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ConfiguredPipelineRuns");
        }
    }
}
