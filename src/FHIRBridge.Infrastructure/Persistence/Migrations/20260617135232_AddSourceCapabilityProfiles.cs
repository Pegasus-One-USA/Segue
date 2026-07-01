using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceCapabilityProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceCapabilityProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FhirVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfiguredScopes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RawCapabilityJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DiscoveredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCapabilityProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCapabilityProfiles_TenantId_SourceConnectionId",
                table: "SourceCapabilityProfiles",
                columns: new[] { "TenantId", "SourceConnectionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceCapabilityProfiles");
        }
    }
}
