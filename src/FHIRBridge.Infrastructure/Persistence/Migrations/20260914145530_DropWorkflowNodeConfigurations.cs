using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropWorkflowNodeConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowNodeConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowNodeConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsSecret = table.Column<bool>(type: "bit", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNodeConfigurations_WorkflowNodes_WorkflowNodeId",
                        column: x => x.WorkflowNodeId,
                        principalTable: "WorkflowNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeConfigurations_WorkflowNodeId",
                table: "WorkflowNodeConfigurations",
                column: "WorkflowNodeId");
        }
    }
}
