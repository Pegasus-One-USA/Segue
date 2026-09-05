using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SyncNotificationHistoryColumns : Migration
    {
        /// <inheritdoc />
        // Guarded raw SQL so re-applying this migration against a database where these columns already
        // exist is a no-op instead of a "column already exists" error.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "NotificationHistory" ADD COLUMN IF NOT EXISTS "AttachmentNames" character varying(1000);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "NotificationHistory" ADD COLUMN IF NOT EXISTS "Body" character varying(8000);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "NotificationHistory" DROP COLUMN IF EXISTS "AttachmentNames";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "NotificationHistory" DROP COLUMN IF EXISTS "Body";
                """);
        }
    }
}
