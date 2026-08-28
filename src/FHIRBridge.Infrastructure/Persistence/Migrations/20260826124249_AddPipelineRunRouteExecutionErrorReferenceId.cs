using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineRunRouteExecutionErrorReferenceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded because two independent migrations added this same column on different
            // branches before the duplicate was caught; this keeps re-application idempotent
            // on any database the duplicate already ran against.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'[PipelineRunRouteExecutions]')
                    AND name = 'ErrorReferenceId'
                )
                BEGIN
                    ALTER TABLE [PipelineRunRouteExecutions] ADD [ErrorReferenceId] nvarchar(50) NULL;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'[PipelineRunRouteExecutions]')
                    AND name = 'ErrorReferenceId'
                )
                BEGIN
                    ALTER TABLE [PipelineRunRouteExecutions] DROP COLUMN [ErrorReferenceId];
                END
                """);
        }
    }
}
