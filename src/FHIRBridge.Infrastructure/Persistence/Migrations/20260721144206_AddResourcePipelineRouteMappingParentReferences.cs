using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourcePipelineRouteMappingParentReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourcePipelineRouteMappingParentReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourcePipelineRouteMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentMappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceFieldOverride = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePipelineRouteMappingParentReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRouteMappingParentReferences_ResourcePipelineRouteMappings_ResourcePipelineRouteMappingId",
                        column: x => x.ResourcePipelineRouteMappingId,
                        principalTable: "ResourcePipelineRouteMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRouteMappingParentReferences_ResourcePipelineRouteMappingId_ParentMappingProfileId",
                table: "ResourcePipelineRouteMappingParentReferences",
                columns: new[] { "ResourcePipelineRouteMappingId", "ParentMappingProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourcePipelineRouteMappingParentReferences");
        }
    }
}
