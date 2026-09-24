using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MakeErrorLogMessageColumnsUnbounded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserFriendlyMessage",
                table: "ErrorLogs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "StackTrace",
                table: "ErrorLogs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "ErrorLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing back can lose data written while the columns were unbounded; existing values are
            // clipped so the ALTER succeeds rather than failing the rollback.
            migrationBuilder.Sql("UPDATE \"ErrorLogs\" SET \"UserFriendlyMessage\" = left(\"UserFriendlyMessage\", 1000) WHERE length(\"UserFriendlyMessage\") > 1000;");
            migrationBuilder.Sql("UPDATE \"ErrorLogs\" SET \"StackTrace\" = left(\"StackTrace\", 4000) WHERE length(\"StackTrace\") > 4000;");
            migrationBuilder.Sql("UPDATE \"ErrorLogs\" SET \"Message\" = left(\"Message\", 2000) WHERE length(\"Message\") > 2000;");

            migrationBuilder.AlterColumn<string>(
                name: "UserFriendlyMessage",
                table: "ErrorLogs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "StackTrace",
                table: "ErrorLogs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "ErrorLogs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
