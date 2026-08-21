using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerResourceTypeSyncCursor : Migration
    {
        // Builds a JSON object mapping every space-separated resource type in RetrievalResourceTypes to the same
        // (single, connection-wide) prior RetrievalLastSuccessfulSyncUtc timestamp — preserves each existing
        // incremental-sync connection's cursor instead of forcing a full re-pull after this deploy. Resource type
        // names are plain FHIR identifiers (e.g. "Patient") with no quotes/backslashes, so no JSON escaping is needed.
        //
        // Uses the classic FOR XML PATH('') string-concatenation trick rather than STRING_AGG: STRING_AGG's
        // expression can't mix an outer-correlated column (t.RetrievalLastSuccessfulSyncUtc) with a column local to
        // the aggregated rowset (STRING_SPLIT's value) — SQL Server rejects that with "Multiple columns are
        // specified in an aggregated expression containing an outer reference". FOR XML PATH concatenation isn't
        // classified as an aggregate, so it has no such restriction.
        private static string BuildBackfillSql(string table) => string.Format(
            """
            UPDATE t
            SET RetrievalLastSuccessfulSyncByResourceType = CASE WHEN p.Pairs IS NULL THEN NULL ELSE '{{' + p.Pairs + '}}' END
            FROM {0} t
            CROSS APPLY (
                SELECT STUFF((
                    SELECT ',"' + RTRIM(LTRIM(s.value)) + '":"' + CONVERT(varchar(33), t.RetrievalLastSuccessfulSyncUtc, 126) + '"'
                    FROM STRING_SPLIT(t.RetrievalResourceTypes, ' ') s
                    WHERE RTRIM(LTRIM(s.value)) <> ''
                    FOR XML PATH(''), TYPE
                ).value('.', 'nvarchar(max)'), 1, 1, '') AS Pairs
            ) p
            WHERE t.RetrievalLastSuccessfulSyncUtc IS NOT NULL
              AND t.RetrievalResourceTypes IS NOT NULL
              AND LTRIM(RTRIM(t.RetrievalResourceTypes)) <> '';
            """,
            table);

        // Reverses the backfill: the earliest per-resource-type watermark becomes the single connection-wide cursor
        // (conservative — never advances the old single cursor past a type that hadn't yet caught up).
        private static string BuildRollbackBackfillSql(string table) => string.Format(
            """
            UPDATE t
            SET RetrievalLastSuccessfulSyncUtc = j.MinDate
            FROM {0} t
            CROSS APPLY (
                SELECT MIN(CONVERT(datetime2, [value])) AS MinDate
                FROM OPENJSON(t.RetrievalLastSuccessfulSyncByResourceType)
            ) j
            WHERE t.RetrievalLastSuccessfulSyncByResourceType IS NOT NULL;
            """,
            table);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RetrievalLastSuccessfulSyncByResourceType",
                table: "SourceConnections",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalLastSuccessfulSyncByResourceType",
                table: "SourceConfigurations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql(BuildBackfillSql("SourceConnections"));
            migrationBuilder.Sql(BuildBackfillSql("SourceConfigurations"));

            migrationBuilder.DropColumn(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConfigurations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.Sql(BuildRollbackBackfillSql("SourceConnections"));
            migrationBuilder.Sql(BuildRollbackBackfillSql("SourceConfigurations"));

            migrationBuilder.DropColumn(
                name: "RetrievalLastSuccessfulSyncByResourceType",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalLastSuccessfulSyncByResourceType",
                table: "SourceConfigurations");
        }
    }
}
