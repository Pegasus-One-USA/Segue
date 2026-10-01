using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
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
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EhrEndpointName",
                table: "WorkflowRuns",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "EhrEndpoints",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JwksUrl",
                table: "EhrEndpoints",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "EhrEndpoints",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PracticeId",
                table: "EhrEndpoints",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenEndpoint",
                table: "EhrEndpoints",
                type: "character varying(500)",
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
