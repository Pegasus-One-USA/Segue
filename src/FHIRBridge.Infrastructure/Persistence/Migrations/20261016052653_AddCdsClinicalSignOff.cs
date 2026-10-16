using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCdsClinicalSignOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignOffNote",
                table: "CdsServiceInstances",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedOffAtUtc",
                table: "CdsServiceInstances",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedOffBy",
                table: "CdsServiceInstances",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignOffNote",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "SignedOffAtUtc",
                table: "CdsServiceInstances");

            migrationBuilder.DropColumn(
                name: "SignedOffBy",
                table: "CdsServiceInstances");
        }
    }
}
