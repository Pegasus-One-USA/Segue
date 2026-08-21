using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldLineageSystemMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationName",
                table: "FieldLineageEntries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationTypeName",
                table: "FieldLineageEntries",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutedAtUtc",
                table: "FieldLineageEntries",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "SourceConnectionName",
                table: "FieldLineageEntries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystemType",
                table: "FieldLineageEntries",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationName",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "DestinationTypeName",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "ExecutedAtUtc",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "SourceConnectionName",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "SourceSystemType",
                table: "FieldLineageEntries");
        }
    }
}
