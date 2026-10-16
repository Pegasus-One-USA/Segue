using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEhrEndpointOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EhrEndpointId",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EhrEndpointName",
                table: "WorkflowRuns",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "EhrEndpoints",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JwksUrl",
                table: "EhrEndpoints",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "EhrEndpoints",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PracticeId",
                table: "EhrEndpoints",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenEndpoint",
                table: "EhrEndpoints",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EhrEndpointId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "EhrEndpointName",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "EhrEndpoints");

            migrationBuilder.DropColumn(
                name: "JwksUrl",
                table: "EhrEndpoints");

            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "EhrEndpoints");

            migrationBuilder.DropColumn(
                name: "PracticeId",
                table: "EhrEndpoints");

            migrationBuilder.DropColumn(
                name: "TokenEndpoint",
                table: "EhrEndpoints");
        }
    }
}
