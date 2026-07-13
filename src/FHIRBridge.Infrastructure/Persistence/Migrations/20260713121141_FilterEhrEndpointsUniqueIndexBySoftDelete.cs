using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FilterEhrEndpointsUniqueIndexBySoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints");

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints",
                columns: new[] { "Vendor", "VendorEndpointId" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints");

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints",
                columns: new[] { "Vendor", "VendorEndpointId" },
                unique: true);
        }
    }
}
