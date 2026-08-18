using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNdcTerminology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CvxCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FullVaccineName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "CvxImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CvxVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NdcImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NdcProducts",
                schema: "terminology",
                columns: table => new
                {
                    ProductNdc = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    GenericName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BrandName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DosageForm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcProducts", x => x.ProductNdc);
                });

            migrationBuilder.CreateTable(
                name: "NdcVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UcumImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UcumUnits",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PrintSymbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumUnits", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "UcumVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CvxCodes_IsActive_Code",
                schema: "terminology",
                table: "CvxCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_CvxImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "CvxImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CvxVersions_IsActive",
                schema: "terminology",
                table: "CvxVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CvxVersions_Version",
                schema: "terminology",
                table: "CvxVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsCodes_IsActive_Code",
                schema: "terminology",
                table: "HcpcsCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "HcpcsImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsVersions_IsActive",
                schema: "terminology",
                table: "HcpcsVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsVersions_Version",
                schema: "terminology",
                table: "HcpcsVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsCodes_IsActive_Code",
                schema: "terminology",
                table: "Icd10PcsCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "Icd10PcsImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsVersions_IsActive",
                schema: "terminology",
                table: "Icd10PcsVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsVersions_Version",
                schema: "terminology",
                table: "Icd10PcsVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NdcImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "NdcImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NdcProducts_IsActive_ProductNdc",
                schema: "terminology",
                table: "NdcProducts",
                columns: new[] { "IsActive", "ProductNdc" });

            migrationBuilder.CreateIndex(
                name: "IX_NdcVersions_IsActive",
                schema: "terminology",
                table: "NdcVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_NdcVersions_Version",
                schema: "terminology",
                table: "NdcVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UcumImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "UcumImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UcumUnits_IsActive_Code",
                schema: "terminology",
                table: "UcumUnits",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_UcumVersions_IsActive",
                schema: "terminology",
                table: "UcumVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_UcumVersions_Version",
                schema: "terminology",
                table: "UcumVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CvxCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "CvxImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "CvxVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NdcImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NdcProducts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NdcVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumUnits",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumVersions",
                schema: "terminology");
        }
    }
}
