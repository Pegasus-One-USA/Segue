using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineRunSqlPersistence : Migration
    {
        /// <inheritdoc />
        // Guarded raw SQL so re-applying this migration against a database where these tables/indexes
        // already exist is a no-op instead of an error.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PipelineRunEvents" (
                    "Id" uuid NOT NULL,
                    "PipelineRunId" uuid NOT NULL,
                    "EventType" character varying(100) NOT NULL,
                    "StepType" character varying(50) NULL,
                    "ResourceType" character varying(200) NULL,
                    "ResourceId" character varying(256) NULL,
                    "Message" text NOT NULL,
                    "CorrelationId" character varying(100) NULL,
                    "OccurredOnUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_PipelineRunEvents" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PipelineRuns" (
                    "Id" uuid NOT NULL,
                    "SourceType" character varying(50) NOT NULL,
                    "DestinationType" character varying(50) NOT NULL,
                    "RequestedResourceTypes" text NOT NULL,
                    "TriggeredBy" character varying(200) NULL,
                    "CorrelationId" character varying(100) NULL,
                    "Status" character varying(50) NOT NULL,
                    "ExtractedResourceCount" integer NOT NULL,
                    "WrittenResourceCount" integer NOT NULL,
                    "FailureMessage" text NULL,
                    "ErrorReferenceId" character varying(50) NULL,
                    "StartedOnUtc" timestamp with time zone NOT NULL,
                    "CompletedOnUtc" timestamp with time zone NULL,
                    CONSTRAINT "PK_PipelineRuns" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PipelineRunSteps" (
                    "Id" uuid NOT NULL,
                    "PipelineRunId" uuid NOT NULL,
                    "StepType" character varying(50) NOT NULL,
                    "Status" character varying(50) NOT NULL,
                    "ResourceType" character varying(200) NULL,
                    "ResourceCount" integer NOT NULL,
                    "Message" text NULL,
                    "StartedOnUtc" timestamp with time zone NOT NULL,
                    "CompletedOnUtc" timestamp with time zone NULL,
                    CONSTRAINT "PK_PipelineRunSteps" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_PipelineRunSteps_PipelineRuns_PipelineRunId" FOREIGN KEY ("PipelineRunId")
                        REFERENCES "PipelineRuns" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRunEvents_OccurredOnUtc" ON "PipelineRunEvents" ("OccurredOnUtc");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRunEvents_PipelineRunId" ON "PipelineRunEvents" ("PipelineRunId");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRuns_CorrelationId" ON "PipelineRuns" ("CorrelationId");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRuns_StartedOnUtc" ON "PipelineRuns" ("StartedOnUtc");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRuns_Status" ON "PipelineRuns" ("Status");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_PipelineRunSteps_PipelineRunId" ON "PipelineRunSteps" ("PipelineRunId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "PipelineRunEvents";
                """);

            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "PipelineRunSteps";
                """);

            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "PipelineRuns";
                """);
        }
    }
}
