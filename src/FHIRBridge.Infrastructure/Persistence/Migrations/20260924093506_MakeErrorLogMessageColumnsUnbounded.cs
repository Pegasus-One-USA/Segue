using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeErrorLogMessageColumnsUnbounded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "ErrorLogs",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "StackTrace",
                table: "ErrorLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserFriendlyMessage",
                table: "ErrorLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing back can lose data written while the columns were unbounded; existing values are
            // clipped so the ALTER succeeds rather than failing the rollback.
            migrationBuilder.Sql("UPDATE [ErrorLogs] SET [Message] = LEFT([Message], 2000) WHERE LEN([Message]) > 2000;");
            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "ErrorLogs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [ErrorLogs] SET [StackTrace] = LEFT([StackTrace], 4000) WHERE LEN([StackTrace]) > 4000;");
            migrationBuilder.AlterColumn<string>(
                name: "StackTrace",
                table: "ErrorLogs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.Sql("UPDATE [ErrorLogs] SET [UserFriendlyMessage] = LEFT([UserFriendlyMessage], 1000) WHERE LEN([UserFriendlyMessage]) > 1000;");
            migrationBuilder.AlterColumn<string>(
                name: "UserFriendlyMessage",
                table: "ErrorLogs",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
