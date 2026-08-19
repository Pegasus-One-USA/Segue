using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeIdentificationProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeIdentificationMethod",
                table: "DestinationConfigurations");

            migrationBuilder.DropColumn(
                name: "RequiresDeIdentification",
                table: "DestinationConfigurations");

            migrationBuilder.AddColumn<Guid>(
                name: "DeIdentificationProfileId",
                table: "TransformationRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionPhase",
                table: "TransformationRules",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PostMapping");

            migrationBuilder.AddColumn<Guid>(
                name: "DeIdentificationProfileId",
                table: "DestinationConfigurations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeIdentificationProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeIdentificationProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_ExecutionPhase_DeIdentificationProfileId_ResourceType",
                table: "TransformationRules",
                columns: new[] { "ExecutionPhase", "DeIdentificationProfileId", "ResourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_DeIdentificationProfiles_Name",
                table: "DeIdentificationProfiles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeIdentificationProfiles");

            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_ExecutionPhase_DeIdentificationProfileId_ResourceType",
                table: "TransformationRules");

            migrationBuilder.DropColumn(
                name: "DeIdentificationProfileId",
                table: "TransformationRules");

            migrationBuilder.DropColumn(
                name: "ExecutionPhase",
                table: "TransformationRules");

            migrationBuilder.DropColumn(
                name: "DeIdentificationProfileId",
                table: "DestinationConfigurations");

            migrationBuilder.AddColumn<string>(
                name: "DeIdentificationMethod",
                table: "DestinationConfigurations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresDeIdentification",
                table: "DestinationConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
