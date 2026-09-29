using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LicenseRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientName = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    CompanyName = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    UniqueKey = table.Column<string>(type: "text", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSubmittedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedManually = table.Column<bool>(type: "boolean", nullable: false),
                    FulfilledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FulfilledIssuedLicenseId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenseRequests_IssuedLicenses_FulfilledIssuedLicenseId",
                        column: x => x.FulfilledIssuedLicenseId,
                        principalTable: "IssuedLicenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_FulfilledAtUtc",
                table: "LicenseRequests",
                column: "FulfilledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_FulfilledIssuedLicenseId",
                table: "LicenseRequests",
                column: "FulfilledIssuedLicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_UniqueKey",
                table: "LicenseRequests",
                column: "UniqueKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseRequests");
        }
    }
}
