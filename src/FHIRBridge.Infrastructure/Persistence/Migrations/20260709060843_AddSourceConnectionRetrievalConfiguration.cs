using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceConnectionRetrievalConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RetrievalIncludeParameters",
                table: "SourceConnections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetrievalIncrementalSyncEnabled",
                table: "SourceConnections",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetrievalMaxRecordsPerRun",
                table: "SourceConnections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalMethod",
                table: "SourceConnections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetrievalPageSize",
                table: "SourceConnections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalResourceTypes",
                table: "SourceConnections",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalRetryPolicy",
                table: "SourceConnections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalRevIncludeParameters",
                table: "SourceConnections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalSearchCriteria",
                table: "SourceConnections",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalSortOrder",
                table: "SourceConnections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetrievalTimeoutSeconds",
                table: "SourceConnections",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetrievalIncludeParameters",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalIncrementalSyncEnabled",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalLastSuccessfulSyncUtc",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalMaxRecordsPerRun",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalMethod",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalPageSize",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalResourceTypes",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalRetryPolicy",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalRevIncludeParameters",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalSearchCriteria",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalSortOrder",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalTimeoutSeconds",
                table: "SourceConnections");
        }
    }
}
