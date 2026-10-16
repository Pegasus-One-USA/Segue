using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCdsEpicAndFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AllowCallback and FetchMissingData default to true for existing rows (the model has no store default,
            // so EF never mistakes an explicit false for 'unset').
            migrationBuilder.AddColumn<bool>(
                name: "AllowCallback",
                table: "CdsTrustedIssuers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedSubject",
                table: "CdsTrustedIssuers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FhirBaseUrl",
                table: "CdsTrustedIssuers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FetchMissingData",
                table: "CdsServiceInstances",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "FixesJson",
                table: "CdsServiceInstances",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinksJson",
                table: "CdsServiceInstances",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverrideReasonsJson",
                table: "CdsServiceInstances",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CallbackMs",
                table: "CdsCallLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CallbackResourceCount",
                table: "CdsCallLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CriteriaId",
                table: "CdsCallLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpicVersion",
                table: "CdsCallLogs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImplementationVersion",
                table: "CdsCallLogs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverrideReasonCodes",
                table: "CdsCallLogs",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuggestionsAccepted",
                table: "CdsCallLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SuggestionsOffered",
                table: "CdsCallLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TriggerAction",
                table: "CdsCallLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowCallback",
                table: "CdsTrustedIssuers");

            migrationBuilder.DropColumn(
                name: "ExpectedSubject",
                table: "CdsTrustedIssuers");

            migrationBuilder.DropColumn(
                name: "FhirBaseUrl",
                table: "CdsTrustedIssuers");

            migrationBuilder.DropColumn(
                name: "FetchMissingData",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "FixesJson",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "LinksJson",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "OverrideReasonsJson",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "CallbackMs",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "CallbackResourceCount",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "CriteriaId",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "EpicVersion",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "ImplementationVersion",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "OverrideReasonCodes",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "SuggestionsAccepted",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "SuggestionsOffered",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "TriggerAction",
                table: "CdsCallLogs");
        }
    }
}
