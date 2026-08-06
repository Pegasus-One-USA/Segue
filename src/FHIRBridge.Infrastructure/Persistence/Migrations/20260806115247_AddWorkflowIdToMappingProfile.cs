using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowIdToMappingProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "MappingProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_ResourceType_SourceConnectionId_DestinationId_WorkflowId",
                table: "MappingProfiles",
                columns: new[] { "ResourceType", "SourceConnectionId", "DestinationId", "WorkflowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MappingProfiles_ResourceType_SourceConnectionId_DestinationId_WorkflowId",
                table: "MappingProfiles");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "MappingProfiles");
        }
    }
}
